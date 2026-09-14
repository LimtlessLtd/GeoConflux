#!/usr/bin/env python3
"""Extract the global coarse place layer from the GeoNames dumps.

Run this to refresh the extract. It is not part of the build: the build is hermetic and never
reaches the network, so the output is committed and this script is how it changes. See
docs/adr/032-global-gazetteer-sourcing.md for why the data moved to GeoNames at this scale, and
docs/adr/033-tiered-gazetteer-artefact.md for why this layer is administrative units and their seats
rather than every settlement on earth.

    python tools/gazetteer/global.py

GeoNames is published under CC BY 4.0. That obligation is discharged by NOTICE at the repository
root and by the credit line the dashboard carries, and the extract states its own provenance in its
header. The dumps are large -- 402 MB and 195 MB compressed -- so they are cached under a working
directory and reused rather than fetched on every run.

    python tools/gazetteer/global.py --cache D:/geonames

The script refuses to write anything it cannot check. Every row must carry a real country code and a
coordinate inside that country's own extent, and a set of anchor spellings must survive the join from
the alternate-name table -- because a join that silently produces nothing looks exactly like a
lexicon with no native spellings in it.
"""

import argparse

import os
import sys
import time
import unicodedata
import urllib.request
import zipfile

BASE = "https://download.geonames.org/export/dump/"
USER_AGENT = "GeoConflux/1.0 (https://github.com/LimitlessLtd/GeoConflux) gazetteer extract"

OUTPUT = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "src", "Geopolitics.Infrastructure", "Location", "Data", "global-places.tsv",
)

# The field separator, and the separator between spellings inside the last field. Neither may occur
# in a name. That is checked rather than assumed, because a single stray tab would shift every
# following column and the rows would still parse.
FIELD = "\t"
SPELLING = "|"

DUMPS = {
    "allCountries.txt": "allCountries.zip",
    "alternateNamesV2.txt": "alternateNamesV2.zip",
}
PLAIN = ["countryInfo.txt"]

# What the coarse layer holds, and at what precision. Administrative units are areas and their
# centroid is representative; a town is a position. The distinction is carried into the lexicon
# rather than flattened, because the dashboard repeats it to the reader.
#
# Rank follows the meaning already established for the theatre extract: smaller is larger. It is what
# lets the merge recognise that a district and the town inside it sharing a name are one place
# described at two scales rather than two places competing for a name.
FEATURES = {
    "ADM1": ("Region", 1),
    "ADM2": ("Region", 2),
    "PPLC": ("Settlement", 3),
    "PPLA": ("Settlement", 3),
    "PPLA2": ("Settlement", 3),
    "PPLG": ("Settlement", 3),
}

# Feature codes that are populated places rather than administrative units. Any of them above the
# population floor is kept, whatever administrative role it does or does not hold.
#
# This exists because the first version of this extract took administrative units and their seats,
# and that assumption is wrong in a way that only a benchmark could have shown. GeoNames codes
# Acapulco -- population 658,609 -- as a plain PPL, while the administrative unit around it is a
# separate record named "Acapulco de Juárez" carrying none of the spellings anybody writes. The city
# was therefore absent and "Acapulco" resolved to nothing. So were Morelia, Khan Yunis, Jabaliyah and
# a great many others: the conflict-coverage benchmark could place only 45% of UCDP's recorded events
# before this rule, and Africa and Asia sat at roughly a quarter.
#
# The lesson is worth keeping rather than only the fix. "Administrative seat" is a role in a national
# scheme, not a synonym for "somewhere people live", and the two diverge exactly where reporting is
# thickest.
SETTLEMENTS = {"PPL", "PPLA", "PPLA2", "PPLA3", "PPLA4", "PPLA5", "PPLC", "PPLG", "PPLS", "STLMT"}

# Languages every place is allowed to carry a spelling in, whatever country it sits in: the ones the
# sources this system reads actually publish in. The rest of a place's spellings come from its own
# country's languages, which GeoNames records in countryInfo.txt -- so a Burmese name is kept for a
# township in Kayin State and a Basque exonym for it is not.
#
# This is the rule the sprint turns on. A conflict is reported in the language it happens in and in
# the language of whoever carried it onward, and those two sets are what a lexicon needs. Every other
# exonym is weight without reach: GeoNames holds 1,027,796 alternate names for the places in this
# tier and this rule keeps the fraction that some source could plausibly use.
WIRE_LANGUAGES = {"en", "fr", "es", "pt", "ru", "ar", "zh"}

# Entries in the alternate-name table that are not names. Codes, links and identifiers all share the
# column with real spellings, and hunting for them in prose would be actively harmful: "link" holds
# URLs, "post" holds postcodes, and an airport code is three letters that occur constantly in text.
NOT_NAMES = {
    "link", "wkdt", "post", "iata", "icao", "faac", "unlc", "tcid", "abbr", "phon", "piny", "nick",
}

# Spellings that must survive the join from the alternate-name table into the place table. Each is a
# judgement a person can make and defend -- that these two strings name one place -- which ADR 026
# permits the editorial layer to do; none of them asserts a coordinate, which it does not.
#
# They exist because the failure mode here is silent. Joining on the wrong column, or filtering the
# languages down to nothing, produces a lexicon that builds, loads, resolves English names and has
# quietly lost every script that made the exercise worth doing.
ANCHORS = [
    ("UA", "Київ"),          # Kyiv, Ukrainian
    ("RU", "Москва"),        # Moscow, Russian
    ("YE", "صنعاء"),         # Sanaa, Arabic
    ("MM", "ရန်ကုန်"),        # Yangon, Burmese
    ("ET", "አዲስ አበባ"),       # Addis Ababa, Amharic
    ("CO", "Bogotá"),        # Bogota, Spanish
    ("CN", "北京"),           # Beijing, Chinese
    ("IR", "تهران"),         # Tehran, Persian
]

# How many inhabitants a place with no administrative role needs before it is kept. Chosen by
# measurement rather than by taste: see docs/adr/033-tiered-gazetteer-artefact.md for what each
# candidate floor was worth against the conflict-coverage benchmark.
POPULATION_FLOOR = 5000

# A coordinate of exactly zero in both axes is the classic sign of a missing value that was written
# as a number anyway. There is ocean at that point and no populated place.
NULL_ISLAND = 0.0005


def fetch(cache, name, archive=None):
    """Return the path to a dump, downloading and unpacking it into the cache if it is not there."""
    target = os.path.join(cache, name)

    if os.path.exists(target):
        return target

    os.makedirs(cache, exist_ok=True)
    source = archive or name
    download = os.path.join(cache, source)

    if not os.path.exists(download):
        print("  downloading %s" % source)
        request = urllib.request.Request(BASE + source, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(request) as response, open(download, "wb") as handle:
            while chunk := response.read(1 << 20):
                handle.write(chunk)

    if archive:
        print("  unpacking %s" % source)
        with zipfile.ZipFile(download) as bundle:
            bundle.extract(name, cache)

    return target


def country_languages(path):
    """Primary language subtags per country, as GeoNames records them."""
    languages = {}

    with open(path, encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("#"):
                continue

            columns = line.rstrip("\n").split("\t")

            if len(columns) < 16 or not columns[0]:
                continue

            # "en-GB,cy-GB,gd" -- the region suffix is not wanted, the language is.
            tags = {tag.split("-")[0] for tag in columns[15].split(",") if tag}
            languages[columns[0]] = tags | WIRE_LANGUAGES

    return languages


def select(path, languages, floor):
    """Read the feature dump once, keeping the tier and the extent of every country in it."""
    places = {}
    extents = {}
    skipped = 0

    with open(path, encoding="utf-8") as handle:
        for line in handle:
            columns = line.rstrip("\n").split("\t")

            if len(columns) < 15:
                continue

            country = columns[8]

            try:
                latitude = float(columns[4])
                longitude = float(columns[5])
            except ValueError:
                continue

            # The extent is taken from every feature of a country, not from the tier, so that the
            # check on a tier row is against something the tier did not choose.
            if country:
                west, south, east, north = extents.get(country, (180.0, 90.0, -180.0, -90.0))
                extents[country] = (
                    min(west, longitude), min(south, latitude),
                    max(east, longitude), max(north, latitude),
                )

            try:
                population = int(columns[14])
            except ValueError:
                population = 0

            feature = FEATURES.get(columns[7])

            # A populated place earns its way in by size when it holds no administrative role.
            if feature is None and columns[7] in SETTLEMENTS and population >= floor:
                feature = ("Settlement", 3)

            if feature is None or not country or country not in languages:
                continue

            if abs(latitude) < NULL_ISLAND and abs(longitude) < NULL_ISLAND:
                skipped += 1
                continue

            precision, rank = feature

            # The administrative codes are what let the merge know that Homs the city sits inside
            # Homs the governorate. Without them containment has to be guessed from how far apart two
            # centroids are, and that guess fails for exactly the units that are large -- which is to
            # say, for first-order units almost everywhere.
            #
            # A unit's own code is the code of the level it is. Homs Governorate is admin1 "HS" with
            # no admin2; Homs city is admin1 "HS" and some admin2 within it. So an ADM1 row keeps its
            # admin1 and drops whatever admin2 the dump happens to carry for it, and an ADM2 row keeps
            # both -- otherwise a unit would not contain itself.
            admin1 = columns[10]
            admin2 = columns[11] if columns[7] != "ADM1" else ""

            places[columns[0]] = {
                "name": columns[1],
                "lat": round(latitude, 4),
                "lon": round(longitude, 4),
                "country": country,
                "precision": precision,
                "rank": rank,
                "population": population,
                "admin1": admin1,
                "admin2": admin2,
                "aliases": set(),
            }

    return places, extents, skipped


def attach_names(path, places, languages):
    """Add every alternate spelling a place's own country or a wire language supplies."""
    kept = 0

    with open(path, encoding="utf-8") as handle:
        for line in handle:
            columns = line.rstrip("\n").split("\t")

            if len(columns) < 4:
                continue

            place = places.get(columns[1])

            if place is None:
                continue

            language, name = columns[2], columns[3]

            # A historic name is what somewhere used to be called. Reporting uses it occasionally and
            # a lexicon carrying it will place Leningrad; it also carries every colonial renaming and
            # every name a place has shed, which is a large amount of ambiguity bought for a small
            # amount of reach. Left out, and stated here rather than left to be inferred.
            if len(columns) > 7 and columns[7] == "1":
                continue

            if language in NOT_NAMES or not name.strip():
                continue

            if language and language not in languages[place["country"]]:
                continue

            place["aliases"].add(name)
            kept += 1

    return kept


def check(places, extents):
    """Refuse the extract unless every row sits in its own country and the name join survived."""
    outside = []

    for identifier, place in places.items():
        west, south, east, north = extents[place["country"]]

        # A tenth of a degree of slack: a country's extent is derived from point features, so a
        # centroid legitimately sits a fraction outside the outermost one.
        if not (west - 0.1 <= place["lon"] <= east + 0.1 and south - 0.1 <= place["lat"] <= north + 0.1):
            outside.append((identifier, place["name"], place["country"]))

    if outside:
        raise SystemExit(
            "%d rows fall outside the extent of the country they claim, the first being %s (%s, %s). "
            "Nothing written." % (len(outside), outside[0][1], outside[0][2], outside[0][0])
        )

    spellings = {}

    for place in places.values():
        for name in place["aliases"] | {place["name"]}:
            if FIELD in name or SPELLING in name:
                raise SystemExit(
                    "The spelling %r contains a separator, so the row it is written on would not "
                    "parse back into the fields it was written from. Nothing written." % name
                )

            spellings.setdefault(place["country"], set()).add(name)

    missing = [
        "%s in %s" % (name, country)
        for country, name in ANCHORS
        if name not in spellings.get(country, ())
    ]

    if missing:
        raise SystemExit(
            "The alternate-name join lost spellings that must be in it: %s. That is the failure this "
            "check exists for -- the extract would still build and still resolve English. Nothing "
            "written." % ", ".join(missing)
        )


def build(places):
    entries = []

    for identifier, place in places.items():
        canonical = place["name"]

        # Normalised to composed form, because the two dumps do not agree on it: a decomposed
        # spelling and a composed one are the same name and must not become two entries.
        aliases = sorted(
            {unicodedata.normalize("NFC", name) for name in place["aliases"]}
            - {unicodedata.normalize("NFC", canonical)}
        )

        entries.append({
            "id": identifier,
            "name": unicodedata.normalize("NFC", canonical),
            "lat": place["lat"],
            "lon": place["lon"],
            "country": place["country"],
            "precision": place["precision"],
            "rank": place["rank"],
            "population": place["population"] or None,
            "admin1": place["admin1"],
            "admin2": place["admin2"],
            "aliases": aliases,
        })

    entries.sort(key=lambda entry: (entry["country"], entry["rank"], entry["name"], entry["id"]))
    return entries


def degrees(value):
    """A coordinate written at the precision it was rounded to, and no wider."""
    return ("%.4f" % value).rstrip("0").rstrip(".") or "0"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--population",
        type=int,
        default=POPULATION_FLOOR,
        help="how many inhabitants a place with no administrative role needs to be kept",
    )
    parser.add_argument(
        "--cache",
        default=os.path.join(os.path.dirname(os.path.abspath(__file__)), ".geonames"),
        help="where the downloaded dumps are kept between runs",
    )
    arguments = parser.parse_args()

    print("Reading the GeoNames dumps. This takes a few minutes and about 2.5 GB of disk.")

    for name in PLAIN:
        fetch(arguments.cache, name)

    paths = {name: fetch(arguments.cache, name, archive) for name, archive in DUMPS.items()}
    languages = country_languages(os.path.join(arguments.cache, "countryInfo.txt"))

    print("  selecting the tier from %d countries" % len(languages))
    places, extents, skipped = select(paths["allCountries.txt"], languages, arguments.population)

    if not places:
        raise SystemExit("The extract is empty, which means the selection is wrong. Nothing written.")

    print("  attaching spellings to %d places" % len(places))
    kept = attach_names(paths["alternateNamesV2.txt"], places, languages)

    check(places, extents)
    entries = build(places)

    by_precision = {}
    for entry in entries:
        by_precision[entry["precision"]] = by_precision.get(entry["precision"], 0) + 1

    countries = len({entry["country"] for entry in entries})

    header = [
        "# Generated by tools/gazetteer/global.py from the GeoNames dumps. Do not edit by hand:",
        "# re-run the script. Coordinates and names come from the dump rather than from anybody's",
        "# recollection, which is the point of sourcing them at all. Every row was checked to fall",
        "# inside the extent of the country it claims, and the spellings that prove the alternate-name",
        "# join survived are asserted rather than hoped for.",
        "#",
        "# Source: GeoNames (https://www.geonames.org), CC BY 4.0, credited in NOTICE at the",
        "# repository root. See docs/adr/032-global-gazetteer-sourcing.md for why this data is",
        "# GeoNames rather than Wikidata, and docs/adr/033-tiered-gazetteer-artefact.md for why it is",
        "# one line per place rather than JSON, and administrative units and their seats rather than",
        "# every settlement on earth.",
        "#",
        "# extracted: %s" % time.strftime("%Y-%m-%d", time.gmtime()),
        "# places: %d in %d countries (%s)"
        % (len(entries), countries, ", ".join("%s %d" % item for item in sorted(by_precision.items()))),
        "#",
        "# name\tlat\tlon\tcountry\tprecision\trank\tpopulation\tadmin1\tadmin2\tspellings separated by |",
    ]

    os.makedirs(os.path.dirname(OUTPUT), exist_ok=True)
    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as handle:
        for line in header:
            handle.write(line + "\n")

        for entry in entries:
            handle.write(FIELD.join([
                entry["name"],
                degrees(entry["lat"]),
                degrees(entry["lon"]),
                entry["country"],
                entry["precision"][0],
                str(entry["rank"]),
                str(entry["population"] or ""),
                entry["admin1"],
                entry["admin2"],
                SPELLING.join(entry["aliases"]),
            ]) + "\n")

    aliases = sum(len(entry["aliases"]) for entry in entries)
    size = os.path.getsize(OUTPUT) / (1024 * 1024)
    print("\nWrote %s" % os.path.normpath(OUTPUT))
    print("  %d places in %d countries" % (len(entries), countries))
    print("  %d alternate spellings kept of %d read" % (aliases, kept))
    print("  %.1f MB" % size)
    print("  %d rows skipped for a null coordinate" % skipped)

    for precision, count in sorted(by_precision.items()):
        print("  %-11s %d" % (precision, count))


if __name__ == "__main__":
    sys.exit(main())
