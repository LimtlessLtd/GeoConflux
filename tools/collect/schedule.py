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


def fingerprint(bundle):
    """What a bundle found, independent of when it went looking.

    The content hashes and nothing else. Every other field a second run of the same brief changes —
    bundleId, collectedAt, every retrievedAt — is about the run rather than about the findings, and
    a hash is over the excerpt, so two bundles with the same fingerprint quote the same posts
    unedited.
    """

    items = bundle.get("items")

    if not isinstance(items, list):
        return frozenset()

    return frozenset(item.get("contentHash") for item in items if item.get("contentHash"))


def verdict(bundle, against=None):
    """Whether this bundle is worth committing, and whether its emptiness is worth shouting about.

    `against` is the bundle already committed for this brief today, when there is one. Returns
    (code, reason). The reason is written to be read in a workflow log by someone who did not run
    the job and is trying to work out whether anything is wrong.
    """

    items = bundle.get("items")
    count = len(items) if isinstance(items, list) else 0
    read, tried = channels_read(bundle)

    if count > 0:
        # A second round on a day that already has one. Re-running a brief an hour later finds the
        # same posts and stamps them with a new retrievedAt, so committing would put a diff of pure
        # timestamps into the history and make `git log data/osint` a record of rounds that ran
        # rather than rounds that found something.
        if against is not None and fingerprint(bundle) == fingerprint(against):
            return QUIET, (
                f"The same {count} item(s) already collected today, re-stamped with this run's "
                "timestamps. Nothing new to commit."
            )

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


def channel_health(directory):
    """Per channel, across every bundle in a directory: how it has been behaving.

    The material for the review the schedule deliberately does not do. A channel that answers 200
    every round and has not contributed an item in a fortnight is not visible from any single
    bundle, from the workflow's exit status, or from the published page — it looks exactly like a
    channel covering a quiet beat. Counting rounds is what separates them.

    This reports; it does not judge. Whether a silent channel should be dropped depends on what the
    brief is for, which is the part that belongs to a person.
    """

    bundles = []

    for name in sorted(os.listdir(directory)):
        if not name.endswith(".json"):
            continue

        try:
            with open(os.path.join(directory, name), encoding="utf-8") as handle:
                bundles.append(json.load(handle))
        except (OSError, ValueError):
            continue

    return summarise(bundles)


def summarise(bundles):
    """The counting half of channel_health, separated so it can be tested without a filesystem."""

    health = {}
    rounds = 0

    for bundle in bundles:
        rounds += 1
        collected_on = str(bundle.get("collectedAt", ""))[:10]

        for source in bundle.get("coverage", {}).get("sources", []):
            channel = source.get("channel")

            if not channel:
                continue

            record = health.setdefault(
                channel,
                {"channel": channel, "rounds": 0, "read": 0, "contributed": 0, "last": None, "posts": 0},
            )

            record["rounds"] += 1

            if source.get("outcome") in READ_OUTCOMES:
                record["read"] += 1
                record["posts"] += source.get("read", 0) or 0

            if (source.get("collected") or 0) > 0:
                record["contributed"] += 1
                record["last"] = collected_on

    return rounds, [health[key] for key in sorted(health)]


def report_health(directory):
    rounds, channels = channel_health(directory)

    if not channels:
        print(f"No bundles in {directory}.")
        return 0

    print(f"{len(channels)} channel(s) across {rounds} bundle(s) in {directory}.\n")
    print(f"{'channel':<34} {'read':>9} {'gave items':>11} {'posts':>7}  last")

    for record in channels:
        read = f"{record['read']}/{record['rounds']}"
        gave = f"{record['contributed']}/{record['rounds']}"
        print(f"{record['channel']:<34} {read:>9} {gave:>11} {record['posts']:>7}  {record['last'] or '-'}")

    # Named rather than left to be spotted in the table. These are the two shapes worth acting on,
    # and both are invisible from any single round.
    mute = [r["channel"] for r in channels if r["read"] == r["rounds"] and r["contributed"] == 0]
    unread = [r["channel"] for r in channels if r["read"] == 0]

    # Named rather than prescribed against. Both shapes have a legitimate answer already recorded in
    # the run files — "a source that is listed and produces nothing is a stated gap; a source quietly
    # dropped from the list is an unstated one" — so reporting the count and stopping is the whole
    # job here. Advising a removal would be arguing with a decision this tool cannot see.
    if mute:
        print(f"\nAnswered every round and contributed nothing: {', '.join(mute)}")
        print("  Read fine and matched nothing. Either the terms miss what the channel publishes,")
        print("  or it does not cover this subject and is carried as a stated gap.")

    if unread:
        print(f"\nNever readable: {', '.join(unread)}")
        print("  Listed in the brief and never actually collected from. Worth confirming the entry")
        print("  is still right, and worth leaving alone if it is kept deliberately as a gap.")

    return 0


def self_test():
    checks = []

    def check(name, condition):
        checks.append((name, bool(condition)))

    def bundle(items=0, outcomes=(), collected="2026-09-14T20:54:19Z", first=0):
        return {
            "collectedAt": collected,
            "items": [
                {"url": f"https://example.test/{n}", "contentHash": f"sha256:{n}"}
                for n in range(first, first + items)
            ],
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

    # 6. A second round on a day that already has one. Same posts, new timestamps: the findings are
    #    what a commit is for, and re-running an hour later has not found anything.
    today = bundle(items=3, outcomes=("collected",))
    restamped = bundle(items=3, outcomes=("collected",), collected="2026-09-14T22:10:00Z")
    code, reason = verdict(restamped, against=today)
    check("re-collecting the same items commits nothing", code == QUIET)
    check("and says the items were already collected", "already collected today" in reason)

    check(
        "a run that found one new item does commit",
        verdict(bundle(items=4, outcomes=("collected",)), against=today)[0] == COMMIT,
    )
    check(
        "a run that found different items commits",
        verdict(bundle(items=3, outcomes=("collected",), first=9), against=today)[0] == COMMIT,
    )
    check(
        "with nothing to compare against, items are committed",
        verdict(today, against=None)[0] == COMMIT,
    )

    # A round that read nothing stays an alarm even when today's bundle already exists. Otherwise a
    # collector that broke after a successful morning round would report a quiet afternoon.
    check(
        "an unreadable round is still an alarm against an existing bundle",
        verdict(bundle(items=0, outcomes=("unreachable",)), against=today)[0] == ALARM,
    )

    # 7. Pruning ages against the bundle's own timestamp, which is the field the runtime ages it by.
    now = dt.datetime(2026, 10, 20, tzinfo=dt.timezone.utc)
    check("a bundle past the horizon is prunable", prunable(bundle(collected="2026-09-14T20:54:19Z"), now))
    check("a bundle inside it is not", not prunable(bundle(collected="2026-10-01T00:00:00Z"), now))

    # The boundary, stated in both directions. Exactly keep_days old is kept: the horizon is the
    # last day a bundle survives, not the first day it dies.
    check("exactly keep-days old is kept", not prunable(bundle(collected="2026-09-22T00:00:00Z"), now, 28))
    check("a day past keep-days is not", prunable(bundle(collected="2026-09-21T00:00:00Z"), now, 28))

    # 8. The horizon is behind the runtime's, so pruning can never remove a bundle still being read.
    check("the working set outlives what the pipeline reads", DEFAULT_KEEP_DAYS > 14)

    # 9. Channel health across rounds. The point of counting rounds rather than reading one bundle:
    #    a channel that answers every time and has never contributed is indistinguishable, in any
    #    single round, from one covering a quiet beat.
    def round_with(*sources):
        return {
            "collectedAt": "2026-09-14T00:00:00Z",
            "coverage": {"sources": [dict(s) for s in sources]},
        }

    rounds, channels = summarise([
        round_with(
            {"channel": "a", "outcome": "collected", "read": 40, "collected": 2},
            {"channel": "b", "outcome": "nothing matched", "read": 40, "collected": 0},
            {"channel": "c", "outcome": "unreachable"},
        ),
        round_with(
            {"channel": "a", "outcome": "nothing matched", "read": 40, "collected": 0},
            {"channel": "b", "outcome": "nothing matched", "read": 40, "collected": 0},
            {"channel": "c", "outcome": "unreachable"},
        ),
    ])

    by_name = {record["channel"]: record for record in channels}
    check("every channel across every round is counted once", rounds == 2 and len(channels) == 3)
    check("a channel that contributed in one round of two says so", by_name["a"]["contributed"] == 1)
    check("and records when it last did", by_name["a"]["last"] == "2026-09-14")
    check("a readable channel that never contributed is read but empty-handed",
          by_name["b"]["read"] == 2 and by_name["b"]["contributed"] == 0)
    check("and its last contribution is nothing rather than a date", by_name["b"]["last"] is None)
    check("an unreachable channel is never counted as read", by_name["c"]["read"] == 0)
    check("posts served are totalled only for rounds that were read", by_name["a"]["posts"] == 80)

    # 10. A bundle with no timestamp is a fault, not a thing to quietly delete.
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
    parser.add_argument("--against", help="the bundle already committed for this brief today, if there is one")
    parser.add_argument("--prunable", help="list the bundles in a directory that have aged out")
    parser.add_argument("--health", help="per-channel behaviour across every bundle in a directory")
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
            fresh = json.load(handle)

        against = None

        # Absent is the ordinary case — the first round of the day has nothing to compare with. A
        # path that was given and cannot be read is left as no comparison rather than failing the
        # round: the worst it costs is a commit of timestamps, and refusing to commit a real
        # collection because an old file is unreadable would be the more expensive mistake.
        if arguments.against and os.path.exists(arguments.against):
            try:
                with open(arguments.against, encoding="utf-8") as handle:
                    against = json.load(handle)
            except (OSError, ValueError):
                against = None

        code, reason = verdict(fresh, against)

        print(reason)
        return code

    if arguments.prunable:
        for path in prunable_files(arguments.prunable, now, arguments.keep_days):
            print(path)

        return 0

    if arguments.health:
        return report_health(arguments.health)

    parser.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
