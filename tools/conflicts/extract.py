#!/usr/bin/env python3
"""Derive the conflict-coverage benchmark's ground truth from the UCDP Georeferenced Event Dataset.

Run this to refresh the benchmark. It is not part of the build: the build is hermetic and never
reaches the network, so the output is committed and this script is how it changes.

    python tools/conflicts/extract.py

The question this exists to answer is not "how well does this system classify" but "how much of the
world's organised violence can it see at all". Answering it honestly needs a register of conflicts
that this repository did not choose, because a benchmark scored against a list its own author wrote
measures nothing.

UCDP is that register. It codes every organised-violence event it can source into named conflicts
with named parties, worldwide, on published criteria, and it is the standard the field actually uses.
Crucially it is obtainable **without a credential**: the UCDP API requires a token, but the flat
dataset downloads do not, which is the same arrangement GeoNames has and the reason Sprint 10 could
go global without waiting for anybody.

The output is one line per conflict, with the place names where that conflict's events actually
happened. The benchmark then asks, per conflict, how many of those names this system could place.
"""

import argparse
import collections
import csv
import io
import os
import sys
import time
import urllib.request
import zipfile

# The most recent complete year in the dataset version below. Stated rather than computed, because a
# benchmark whose scope silently moves is a benchmark whose numbers cannot be compared across runs.
YEAR = 2024
VERSION = "25.1"

DUMP = "GEDEvent_v25_1.csv"
ARCHIVE = "ged251-csv.zip"
BASE = "https://ucdp.uu.se/downloads/ged/"
USER_AGENT = "GeoConflux/1.0 (https://github.com/LimitlessLtd/GeoConflux) conflict benchmark"

# One file, two consumers. The register is the benchmark's ground truth and it is also the seed the
# running system starts from, and those must be the same list: a benchmark that scored coverage
# against a different register from the one the system watches would measure nothing.
OUTPUT = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "data", "conflicts", "ucdp-%d.tsv" % YEAR,
)

FIELD = "\t"
NAME = "|"

# The three fields UCDP uses to say where an event happened, most specific first. This is the same
# order and the same fallback UcdpResponseParser already applies, so the benchmark measures the path
# the system would actually take rather than an idealised one.
PLACE_FIELDS = ("where_coordinates", "adm_2", "adm_1")

# UCDP's own coding of what kind of violence a conflict is. Carried because the three mean genuinely
# different things and a reader comparing coverage across them should be able to see which is which.
VIOLENCE = {"1": "state-based", "2": "non-state", "3": "one-sided"}


def fetch(cache):
    """Return the path to the event dump, downloading and unpacking it into the cache if needed."""
    target = os.path.join(cache, DUMP)

    if os.path.exists(target):
        return target

    os.makedirs(cache, exist_ok=True)
    download = os.path.join(cache, ARCHIVE)

    if not os.path.exists(download):
        print("  downloading %s (about 29 MB)" % ARCHIVE)
        request = urllib.request.Request(BASE + ARCHIVE, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(request) as response, open(download, "wb") as handle:
            while chunk := response.read(1 << 20):
                handle.write(chunk)

    print("  unpacking %s" % ARCHIVE)
    with zipfile.ZipFile(download) as bundle:
        bundle.extract(DUMP, cache)

    return target


def collect(path):
    """Group one year of coded events into the conflicts they belong to."""
    conflicts = {}

    with io.open(path, encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            if int(row["year"]) != YEAR:
                continue

            conflict = conflicts.setdefault(row["conflict_new_id"], {
                "name": row["conflict_name"],
                "side_a": row["side_a"],
                "side_b": row["side_b"],
                "region": row["region"],
                "violence": VIOLENCE.get(row["type_of_violence"], "unstated"),
                "countries": collections.Counter(),
                "places": collections.Counter(),
                "events": 0,
                "deaths": 0,
            })

            conflict["events"] += 1
            conflict["deaths"] += int(row["best"] or 0)
            conflict["countries"][row["country"]] += 1

            # One name per event, chosen the way the adapter would choose it. Counting every field
            # would credit this system for placing a province when the event names a village.
            for field in PLACE_FIELDS:
                if row.get(field, "").strip():
                    conflict["places"][row[field].strip()] += 1
                    break

    return conflicts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--cache",
        default=os.path.join(os.path.dirname(os.path.abspath(__file__)), ".ucdp"),
        help="where the downloaded dataset is kept between runs",
    )
    arguments = parser.parse_args()

    print("Reading the UCDP Georeferenced Event Dataset v%s." % VERSION)
    conflicts = collect(fetch(arguments.cache))

    if not conflicts:
        raise SystemExit("No conflicts were read, which means the filter is wrong. Nothing written.")

    rows = sorted(conflicts.values(), key=lambda c: (-c["events"], c["name"]))

    for conflict in rows:
        for text in [conflict["name"], conflict["side_a"], conflict["side_b"]] + list(conflict["places"]):
            if FIELD in text or NAME in text:
                raise SystemExit(
                    "The value %r contains a separator, so the row it is written on would not parse "
                    "back into the fields it was written from. Nothing written." % text
                )

    header = [
        "# Generated by tools/conflicts/extract.py from the UCDP Georeferenced Event Dataset v%s." % VERSION,
        "# Do not edit by hand: re-run the script.",
        "#",
        "# This is the benchmark's ground truth, and it is deliberately not a list this repository",
        "# wrote. It is every conflict UCDP recorded an event for in %d -- %d of them -- with the" % (YEAR, len(rows)),
        "# place names those events actually happened at. A system that can see three of these is a",
        "# system that can see three of these, however well it does on the three.",
        "#",
        "# Source: UCDP (https://ucdp.uu.se), CC BY 4.0, credited in NOTICE at the repository root.",
        "# Sundberg & Melander, Journal of Peace Research 50(4); Davies, Engstrom, Pettersson &",
        "# Oberg, Journal of Peace Research 62(4).",
        "#",
        "# year: %d" % YEAR,
        "# conflicts: %d, events: %d, deaths: %d"
        % (len(rows), sum(c["events"] for c in rows), sum(c["deaths"] for c in rows)),
        "#",
        "# id\tname\tside_a\tside_b\tregion\tviolence\tcountry\tevents\tdeaths\tplaces as name*count separated by |",
    ]

    os.makedirs(os.path.dirname(OUTPUT), exist_ok=True)

    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as handle:
        for line in header:
            handle.write(line + "\n")

        for identifier, conflict in sorted(
            conflicts.items(), key=lambda item: (-item[1]["events"], item[1]["name"])
        ):
            handle.write(FIELD.join([
                identifier,
                conflict["name"],
                conflict["side_a"],
                conflict["side_b"],
                conflict["region"],
                conflict["violence"],
                conflict["countries"].most_common(1)[0][0],
                str(conflict["events"]),
                str(conflict["deaths"]),
                NAME.join(
                    "%s*%d" % (place, count)
                    for place, count in sorted(conflict["places"].items(), key=lambda p: (-p[1], p[0]))
                ),
            ]) + "\n")

    size = os.path.getsize(OUTPUT) / 1024
    tail = sum(1 for c in rows if c["events"] < 25)

    print("\nWrote %s" % os.path.normpath(OUTPUT))
    print("  %d conflicts in %d, %d events, %d deaths"
          % (len(rows), YEAR, sum(c["events"] for c in rows), sum(c["deaths"] for c in rows)))
    print("  %d of them recorded fewer than 25 events -- the tail this benchmark is really about" % tail)
    print("  %d distinct place names, %.0f KB"
          % (len({p for c in rows for p in c["places"]}), size))


if __name__ == "__main__":
    sys.exit(main())
