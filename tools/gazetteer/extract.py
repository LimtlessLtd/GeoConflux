#!/usr/bin/env python3
"""Extract theatre place names from Wikidata into the committed gazetteer data file.

Run this to refresh the extract. It is not part of the build: the build is hermetic and never
reaches the network, so the output is committed and this script is how it changes. See
docs/adr/026-gazetteer-sourcing.md for why the data is sourced this way rather than hand-written.

    python tools/gazetteer/extract.py

Wikidata content is CC0, which is the property that made it the choice here. The script asks for
exactly what the sprint needs — a coordinate and the names the reporting actually uses, in the
scripts it uses them in — and refuses to write anything it cannot place inside the theatre it claims
to describe.
"""

import json
import os
import subprocess
import sys
import time
import unicodedata

ENDPOINT = "https://query.wikidata.org/sparql"
USER_AGENT = "GeoConflux/1.0 (https://github.com/LimitlessLtd/GeoConflux) gazetteer extract"

OUTPUT = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "src", "Geopolitics.Infrastructure", "Location", "Data", "theatre-places.json",
)

# Every row is checked against the box of the theatre it claims to belong to, and a single row
# outside it stops the whole extract. This exists because of a real mistake: an earlier query asked
# for settlements in Q207521, which reads plausibly as Tigray and is in fact the Ethiopian Empire, a
# state that ended in 1974. It returned real Ethiopian places with real coordinates, hundreds of
# kilometres from Tigray, and nothing about the result set looked wrong.
BOXES = {
    "Ukraine": (21.5, 43.9, 40.5, 52.7),
    "Yemen": (41.5, 12.0, 54.8, 19.2),
    "Tigray": (36.2, 12.0, 40.6, 15.2),
}

# Where a query is allowed to look, which is not the same thing as where a theatre is. Ukraine's
# validation box is the whole country, because the large-city rule legitimately returns Lviv; its
# selection box is the east and south, because that is where the war is and where the small-town
# floor is worth paying for. Conflating the two is a mistake that reads as working: the selection
# silently widens to the whole country and the validation still passes.
SELECTION_BOXES = {
    "Ukraine": (30.0, 44.0, 40.5, 52.5),
    "Yemen": BOXES["Yemen"],
    "Tigray": BOXES["Tigray"],
}

# Languages worth asking for per theatre: the ones the reporting is actually written in. Latin
# transliterations of Tigrinya and Amharic names are genuinely unstable (Mekelle / Mekele / Mek'ele /
# Makale), which is why alternate labels are collected as well as preferred ones.
LANGUAGES = {
    "Ukraine": ["en", "uk", "ru"],
    "Yemen": ["en", "ar"],
    "Tigray": ["en", "ti", "am"],
}

SETTLEMENT = "?item wdt:P31/wdt:P279* wd:Q486972 ."

# How many items to ask for labels about at once. The endpoint is public and shared, and asking for
# selection and labels in one query times out: a few thousand places with a dozen names each is tens
# of thousands of rows. Two cheap passes are kinder than one expensive one that has to be retried.
LABEL_BATCH = 250


def box_clause(box):
    west, south, east, north = box
    return """
  SERVICE wikibase:box {
    ?item wdt:P625 ?coord .
    bd:serviceParam wikibase:cornerSouthWest "Point(%s %s)"^^geo:wktLiteral .
    bd:serviceParam wikibase:cornerNorthEast "Point(%s %s)"^^geo:wktLiteral .
  }
""" % (west, south, east, north)


# How large an administrative unit is, smaller number meaning larger unit. It is carried because
# nested units share a name constantly — Marib is a governorate, a district and a city — and a merge
# that treated those as "this name means two different places" would drop the most-reported place in
# a theatre. Rank is what lets the merge prefer the containing unit instead.
RANK_REGION = 1
RANK_DISTRICT = 2
RANK_SETTLEMENT = 3


def selection(theatre, body, precision, rank):
    """Phase one: which places, where, how large, and how many people. No labels, so this is cheap."""
    return {
        "theatre": theatre,
        "precision": precision,
        "rank": rank,
        "sparql": """
SELECT DISTINCT ?item ?lat ?lon ?pop WHERE {
%s
  ?item p:P625/psv:P625 ?node .
  ?node wikibase:geoLatitude ?lat ; wikibase:geoLongitude ?lon .
  OPTIONAL { ?item wdt:P1082 ?pop }
}
""" % body,
    }


LABEL_QUERY = """
SELECT ?item ?label ?alt WHERE {
  VALUES ?item { %s }
  OPTIONAL { ?item skos:altLabel ?alt . FILTER(LANG(?alt) IN (%s)) }
  ?item rdfs:label ?label . FILTER(LANG(?label) IN (%s))
}
"""


QUERIES = [
    # Ukraine: the war zone down to small towns, plus the large cities anywhere in the country. The
    # floor admits the front-line towns that reporting names and excludes the hamlets it rarely does.
    selection("Ukraine", """
%s
  %s
  ?item wdt:P17 wd:Q212 .
  ?item wdt:P1082 ?pop . FILTER(?pop >= 1000)
""" % (box_clause(SELECTION_BOXES["Ukraine"]), SETTLEMENT), "Settlement", RANK_SETTLEMENT),

    selection("Ukraine", """
%s
  %s
  ?item wdt:P17 wd:Q212 .
  ?item wdt:P1082 ?pop . FILTER(?pop >= 50000)
""" % (box_clause(BOXES["Ukraine"]), SETTLEMENT), "Settlement", RANK_SETTLEMENT),

    # Yemen: districts and governorates first, because that is the precision the reporting supports.
    # A governorate is an area rather than a position, so it is marked Region.
    selection("Yemen", "  ?item wdt:P31 wd:Q6617100 .", "Region", RANK_DISTRICT),
    selection("Yemen", "  ?item wdt:P31 wd:Q331130 .", "Region", RANK_REGION),

    selection("Yemen", """
%s
  %s
  ?item wdt:P17 wd:Q805 .
  ?item wdt:P1082 ?pop . FILTER(?pop >= 20000)
""" % (box_clause(SELECTION_BOXES["Yemen"]), SETTLEMENT), "Settlement", RANK_SETTLEMENT),

    # Tigray: everything with a coordinate. It is small, and it is the theatre that resolves nothing
    # at all today.
    selection("Tigray", """
%s
  %s
  ?item wdt:P17 wd:Q115 .
""" % (box_clause(SELECTION_BOXES["Tigray"]), SETTLEMENT), "Settlement", RANK_SETTLEMENT),

    # Woredas, the Ethiopian third-level district. Reporting from Tigray names them as often as it
    # names towns — the January 2026 clashes at Mai Degusha were reported as Tselemti — and a
    # settlement-only query finds none of them.
    selection("Tigray", """
%s
  ?item wdt:P31 wd:Q690840 .
""" % box_clause(SELECTION_BOXES["Tigray"]), "Region", RANK_DISTRICT),
]


def ask(sparql, attempts=5, label=""):
    for attempt in range(attempts):
        result = subprocess.run(
            [
                "curl", "-s", "-G", ENDPOINT,
                "--data-urlencode", "query=" + sparql,
                "-H", "Accept: application/sparql-results+json",
                "-H", "User-Agent: " + USER_AGENT,
                "--max-time", "300",
            ],
            capture_output=True, text=True, encoding="utf-8",
        )
        try:
            return json.loads(result.stdout)["results"]["bindings"]
        except Exception:
            if attempt == attempts - 1:
                raise SystemExit(
                    "Wikidata did not answer after %d attempts for %s. Last reply: %r"
                    % (attempts, label or "a query", (result.stdout or "")[:200].replace("\n", " "))
                )
            # The public endpoint sheds load under pressure; backing off is the polite response.
            print("    retrying %s (attempt %d)" % (label or "query", attempt + 2))
            time.sleep(15 * (attempt + 1))


def normalise(value):
    """NFC, so the committed file holds one spelling of each character rather than several."""
    return unicodedata.normalize("NFC", value).strip()


def usable(name):
    """Reject names that cannot work as search terms or that are identifiers rather than names."""
    if len(name) < 2 or len(name) > 60:
        return False

    # A Wikidata item with no label falls back to its Q-number, which is not a place name.
    if name.startswith("Q") and name[1:].isdigit():
        return False

    return any(character.isalpha() for character in name)


def fetch_labels(items, theatre):
    """Phase two: the names each place is written by, in the languages that report it."""
    langs = ", ".join('"%s"' % code for code in LANGUAGES[theatre])
    names = {}

    for start in range(0, len(items), LABEL_BATCH):
        batch = items[start:start + LABEL_BATCH]
        values = " ".join("wd:" + item for item in batch)

        for row in ask(LABEL_QUERY % (values, langs, langs), label="%s names %d+" % (theatre, start)):
            item = row["item"]["value"].rsplit("/", 1)[-1]

            for key in ("label", "alt"):
                if key in row:
                    name = normalise(row[key]["value"])
                    if usable(name):
                        bucket = names.setdefault(item, {"preferred": {}, "alt": set()})
                        if key == "label":
                            bucket["preferred"].setdefault(row[key]["xml:lang"], set()).add(name)
                        else:
                            bucket["alt"].add(name)

    return names


def collect():
    places = {}

    for spec in QUERIES:
        theatre = spec["theatre"]
        west, south, east, north = BOXES[theatre]
        rows = ask(spec["sparql"], label="%s selection" % theatre)
        print("  %-8s %-10s %5d places" % (theatre, spec["precision"], len(rows)))

        for row in rows:
            item = row["item"]["value"].rsplit("/", 1)[-1]
            latitude = round(float(row["lat"]["value"]), 5)
            longitude = round(float(row["lon"]["value"]), 5)

            if not (south <= latitude <= north and west <= longitude <= east):
                raise SystemExit(
                    "%s claims %s at %s,%s, which is outside its bounding box. Refusing to write a "
                    "file that places a theatre somewhere it is not." % (theatre, item, latitude, longitude)
                )

            population = int(float(row["pop"]["value"])) if "pop" in row else None

            # setdefault, so the first rule to claim an item keeps it. An item can satisfy more than
            # one rule — a district that is also typed as a settlement — and the rank recorded should
            # be the one the rule that found it intended.
            places.setdefault(item, {
                "theatre": theatre,
                "precision": spec["precision"],
                "rank": spec["rank"],
                "lat": latitude,
                "lon": longitude,
                "population": population,
                "names": {"preferred": {}, "alt": set()},
            })

    by_theatre = {}
    for item, place in places.items():
        by_theatre.setdefault(place["theatre"], []).append(item)

    for theatre, items in sorted(by_theatre.items()):
        print("  fetching names for %d %s places" % (len(items), theatre))
        for item, names in fetch_labels(items, theatre).items():
            places[item]["names"] = names

    return places


def build(places):
    entries = []

    for item, place in places.items():
        preferred = place["names"]["preferred"]

        # The canonical name is the source's preferred English label where there is one, because the
        # rest of the lexicon and the dashboard are in English and this is the name a reader will
        # recognise. Taking the alphabetically first of every recorded spelling instead would title
        # Mekelle as "Makale". Failing that, a preferred label in any language, so a place with only a
        # local name still resolves rather than being dropped for want of a translation.
        english = sorted(preferred.get("en", set()))

        if english:
            canonical = english[0]
        else:
            everything = sorted(name for names in preferred.values() for name in names)
            if not everything:
                continue
            canonical = everything[0]

        spellings = {name for names in preferred.values() for name in names} | place["names"]["alt"]
        aliases = sorted(spellings - {canonical})

        entries.append({
            "id": item,
            "name": canonical,
            "lat": place["lat"],
            "lon": place["lon"],
            "country": {"Ukraine": "UA", "Yemen": "YE", "Tigray": "ET"}[place["theatre"]],
            "theatre": place["theatre"],
            "precision": place["precision"],
            "rank": place["rank"],
            "population": place["population"],
            "aliases": aliases,
        })

    entries.sort(key=lambda entry: (entry["theatre"], entry["name"], entry["id"]))
    return entries


def main():
    print("Querying Wikidata. This takes a few minutes.")
    entries = build(collect())

    if not entries:
        raise SystemExit("The extract is empty, which means the queries are wrong. Nothing written.")

    by_theatre = {}
    for entry in entries:
        by_theatre[entry["theatre"]] = by_theatre.get(entry["theatre"], 0) + 1

    document = {
        "$comment": (
            "Generated by tools/gazetteer/extract.py from Wikidata, whose content is published under "
            "CC0. Do not edit by hand: re-run the script. Coordinates and names come from the query "
            "rather than from anybody's recollection, which is the point of sourcing them at all. "
            "Every row was checked to fall inside the bounding box of the theatre it claims. See "
            "docs/adr/026-gazetteer-sourcing.md."
        ),
        "source": "Wikidata (https://www.wikidata.org), CC0",
        "extracted": time.strftime("%Y-%m-%d", time.gmtime()),
        "counts": by_theatre,
        "places": entries,
    }

    os.makedirs(os.path.dirname(OUTPUT), exist_ok=True)
    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(document, handle, ensure_ascii=False, indent=1, sort_keys=False)
        handle.write("\n")

    aliases = sum(len(entry["aliases"]) for entry in entries)
    size = os.path.getsize(OUTPUT) / 1024
    print("\nWrote %s" % os.path.normpath(OUTPUT))
    print("  %d places, %d alternate spellings, %.0f KB" % (len(entries), aliases, size))
    for theatre, count in sorted(by_theatre.items()):
        print("  %-8s %d" % (theatre, count))


if __name__ == "__main__":
    sys.exit(main())
