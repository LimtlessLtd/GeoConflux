#!/usr/bin/env python3
"""What an unattended collection run does with what it collected.

Collecting is `collect.py`. This is the part that only matters when nobody is watching: whether a
freshly collected bundle is worth committing, whether an old one has aged out of the working set,
and — the one that actually keeps this honest — whether a run that produced nothing found a quiet
week or a broken collector.

Those two cases look identical from the outside. Both end with an empty bundle and a green run. One
of them means every channel was read and none of them published anything matching the brief; the
other means the collector has silently stopped reaching anything at all, and a scheduled job that
fails that way keeps reporting success while the dashboard slowly expires. Telling them apart is the
reason this file exists.

So the decisions are functions of their inputs with an offline self-test, rather than lines of shell
inside a workflow — for the same reason the access policy is code and not a paragraph in a brief.
See docs/adr/030-collection-access-policy.md and docs/adr/038-collection-without-being-asked.md.

    python tools/collect/schedule.py --self-test
    python tools/collect/schedule.py --verdict data/osint/2026-09-14-armed-conflict.json
    python tools/collect/schedule.py --prunable data/osint --keep-days 28

Exit codes from --verdict are what the workflow branches on:

    0   commit it
    3   nothing to commit, and that is a fact about the week rather than a fault
    4   nothing to commit, and nothing was read either — fail the run loudly
"""

import argparse
import datetime as dt
import json
import os
import sys

# The outcomes collect.py records per channel. Two of them mean the channel served us something we
# could read; the rest mean we never got that far. The vocabulary lives at the `outcomes.append`
# calls in collect.py — this is a second copy, so the self-test below pins the property that matters
# rather than the list: anything unrecognised counts as NOT read.
#
# That default is the whole point. If collect.py grows a new failure outcome and this file is not
# updated, the conservative reading raises a false alarm, which someone then fixes. The optimistic
# reading would silence a real one, which nobody would ever see.
READ_OUTCOMES = frozenset({"collected", "nothing matched", "capped"})
UNREAD_OUTCOMES = frozenset({"unreachable", "no public posts"})

COMMIT = 0
QUIET = 3
ALARM = 4

# Twice the runtime's MaxBundleAge of 14 days (AgentBriefOptions.cs). The runtime stopped reading a
# bundle a fortnight before this removes it from the working tree, so pruning can never take
# something the pipeline was still using, and raising MaxBundleAge as far as 28 days would not
# silently start deleting live data.
#
# Nothing is destroyed by this. Every bundle stays in the repository's history permanently, which is
# where an archive belongs; what is removed is a file the pipeline parses on every poll and then
# skips. Unbounded accumulation is not automation, it is manual work deferred.
DEFAULT_KEEP_DAYS = 28


def collected_at(bundle):
    """When the run that produced this bundle happened, as the runtime reads it."""

    # The same field AgentBriefEventSource ages a bundle against, rather than the date in the
    # filename. A name can be wrong; this is what the pipeline actually acts on.
    stamp = bundle.get("collectedAt")

    if not isinstance(stamp, str) or not stamp:
        raise ValueError("the bundle records no collectedAt")

    # fromisoformat only learned to read a trailing Z in 3.11, and this runs on whatever Python the
    # runner and the developer's machine happen to have.
    return dt.datetime.fromisoformat(stamp.replace("Z", "+00:00"))


def channels_read(bundle):
    """How many channels actually served something, and how many were tried at all."""

    sources = bundle.get("coverage", {}).get("sources", [])

    if not isinstance(sources, list):
        return 0, 0

    read = sum(1 for source in sources if source.get("outcome") in READ_OUTCOMES)
    return read, len(sources)


def verdict(bundle):
    """Whether this bundle is worth committing, and whether its emptiness is worth shouting about.

    Returns (code, reason). The reason is written to be read in a workflow log by someone who did
    not run the job and is trying to work out whether anything is wrong.
    """

    items = bundle.get("items")
    count = len(items) if isinstance(items, list) else 0
    read, tried = channels_read(bundle)

    if count > 0:
        return COMMIT, f"{count} item(s) from {read} of {tried} channel(s)."

    # Nothing collected. Which of the two failures it is depends entirely on whether anybody
    # answered, and this is the only place that distinction gets made.
    if tried == 0:
        return ALARM, "The run recorded no channels at all, so the brief collected from nothing."

    if read == 0:
        return ALARM, (
            f"None of {tried} channel(s) could be read, so this is a statement about the collector "
            "rather than about the week. Bundle not committed."
        )

    return QUIET, (
        f"{read} of {tried} channel(s) were read and none published anything matching the brief. "
        "Nothing to commit, which is a fact about the week."
    )


def prunable(bundle, now, keep_days=DEFAULT_KEEP_DAYS):
    """Whether a committed bundle has aged out of the working set."""

    return (now - collected_at(bundle)).days > keep_days


def prunable_files(directory, now, keep_days=DEFAULT_KEEP_DAYS):
    """The bundles in a directory that have aged out, oldest name first.

    A bundle that cannot be read is never returned. An unreadable file is something to look at, and
    deleting it on a schedule would remove the evidence of whatever went wrong with it.
    """

    stale = []

    for name in sorted(os.listdir(directory)):
        if not name.endswith(".json"):
            continue

        path = os.path.join(directory, name)

        try:
            with open(path, encoding="utf-8") as handle:
                bundle = json.load(handle)

            if prunable(bundle, now, keep_days):
                stale.append(path)
        except (OSError, ValueError):
            continue

    return stale


def self_test():
    checks = []

    def check(name, condition):
        checks.append((name, bool(condition)))

    def bundle(items=0, outcomes=(), collected="2026-09-14T20:54:19Z"):
        return {
            "collectedAt": collected,
            "items": [{"url": f"https://example.test/{index}"} for index in range(items)],
            "coverage": {"sources": [{"channel": f"c{i}", "outcome": o} for i, o in enumerate(outcomes)]},
        }

    # 1. A bundle with items is committed, however few and however many channels were silent.
    code, _ = verdict(bundle(items=1, outcomes=("collected", "unreachable", "nothing matched")))
    check("one collected item is worth committing", code == COMMIT)

    # 2. Read everything, matched nothing. A quiet week is not a fault, and must not fail a run —
    #    a scheduled job that goes red because the world was calm gets muted, and then the real
    #    failures are muted too.
    code, reason = verdict(bundle(items=0, outcomes=("nothing matched", "nothing matched")))
    check("a read-but-empty run is quiet, not broken", code == QUIET)
    check("and says so as a fact about the week", "about the week" in reason)

    # 3. Nothing could be read. Identical output to case 2 — empty bundle, no error thrown — and the
    #    opposite meaning. This is the case the whole file exists for.
    code, reason = verdict(bundle(items=0, outcomes=("unreachable", "no public posts", "unreachable")))
    check("a run that read nothing is an alarm", code == ALARM)
    check("and blames the collector rather than the week", "rather than about the week" in reason)

    # 4. A run with no channels at all. A truncated or misconfigured run file reaches nothing, and
    #    would otherwise present as the quietest possible week.
    check("a run against no channels is an alarm", verdict(bundle(items=0))[0] == ALARM)

    # 5. An outcome this file has never heard of does not count as a successful read. If collect.py
    #    grows a new way to fail, the conservative reading raises a false alarm someone fixes; the
    #    optimistic one hides a real one forever.
    check(
        "an unrecognised outcome is not counted as read",
        verdict(bundle(items=0, outcomes=("teapot", "teapot")))[0] == ALARM,
    )
    check(
        "the two outcome sets do not overlap",
        not (READ_OUTCOMES & UNREAD_OUTCOMES),
    )

    # 6. Pruning ages against the bundle's own timestamp, which is the field the runtime ages it by.
    now = dt.datetime(2026, 10, 20, tzinfo=dt.timezone.utc)
    check("a bundle past the horizon is prunable", prunable(bundle(collected="2026-09-14T20:54:19Z"), now))
    check("a bundle inside it is not", not prunable(bundle(collected="2026-10-01T00:00:00Z"), now))

    # The boundary, stated in both directions. Exactly keep_days old is kept: the horizon is the
    # last day a bundle survives, not the first day it dies.
    check("exactly keep-days old is kept", not prunable(bundle(collected="2026-09-22T00:00:00Z"), now, 28))
    check("a day past keep-days is not", prunable(bundle(collected="2026-09-21T00:00:00Z"), now, 28))

    # 7. The horizon is behind the runtime's, so pruning can never remove a bundle still being read.
    check("the working set outlives what the pipeline reads", DEFAULT_KEEP_DAYS > 14)

    # 8. A bundle with no timestamp is a fault, not a thing to quietly delete.
    try:
        collected_at({"items": []})
        check("a bundle with no collectedAt is refused", False)
    except ValueError:
        check("a bundle with no collectedAt is refused", True)

    failures = [name for name, passed in checks if not passed]

    for name, passed in checks:
        print(f"{'ok  ' if passed else 'FAIL'} {name}")

    print(f"\n{len(checks) - len(failures)}/{len(checks)} passed")
    return 1 if failures else 0


def main():
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--self-test", action="store_true", help="run the offline checks and exit")
    parser.add_argument("--verdict", help="decide whether one freshly collected bundle is worth committing")
    parser.add_argument("--prunable", help="list the bundles in a directory that have aged out")
    parser.add_argument(
        "--keep-days",
        type=int,
        default=DEFAULT_KEEP_DAYS,
        help=f"how long a bundle stays in the working tree (default {DEFAULT_KEEP_DAYS})",
    )
    parser.add_argument("--now", help="the instant to age against, for a reproducible run")
    arguments = parser.parse_args()

    if arguments.self_test:
        return self_test()

    now = (
        dt.datetime.fromisoformat(arguments.now.replace("Z", "+00:00"))
        if arguments.now
        else dt.datetime.now(dt.timezone.utc)
    )

    if arguments.verdict:
        with open(arguments.verdict, encoding="utf-8") as handle:
            code, reason = verdict(json.load(handle))

        print(reason)
        return code

    if arguments.prunable:
        for path in prunable_files(arguments.prunable, now, arguments.keep_days):
            print(path)

        return 0

    parser.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
