#!/usr/bin/env python3
"""Access policy for collection runs: honour robots.txt, identify the request, rate limit, record refusals.

Collection reads what a service chooses to serve publicly and stops there. This module is where that
sentence becomes code, so that no platform reader has to remember it.

    python tools/collect/access.py --self-test

The self-test runs offline and is a CI step. Nothing here that decides anything — whether a path is
allowed, how long to wait, whether a status is a refusal — touches the network, which is what lets
those decisions be asserted rather than described. See docs/adr/030-collection-access-policy.md.

Four rules, and the second and third are the ones that are easy to get wrong:

1. A missing robots.txt permits everything. RFC 9309 section 2.3.1.3; an absent file is not a
   refusal, and treating it as one would block hosts that simply never wrote one.
2. An *unreachable* robots.txt forbids everything. Same RFC, and the opposite answer. A 500 or a
   timeout means the rules are unknown, and proceeding on the assumption that unknown rules permit
   what you want is the reasoning every badly-behaved crawler uses.
3. An authentication wall, a paywall, or a payment gate is a refusal and is recorded as one. It is
   never retried with different headers, a different agent, or a logged-out workaround. A source
   behind a gate is a Tier C entry, not a challenge.
4. Every request says who it is and waits its turn.
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import urllib.robotparser

# Names the project and points at it. An automated reader that will not say what it is has already
# decided it does not want to be asked to stop, and a contact URL is what makes a rate limit a
# conversation rather than a block.
USER_AGENT = "GeoConflux/1.0 (+https://github.com/LimitlessLtd/GeoConflux) OSINT collection"

# Floor between two requests to one host, in seconds. A published crawl-delay raises this and never
# lowers it: a host saying "one second is fine" is not a reason to go faster than this project has
# decided to go.
DEFAULT_INTERVAL = 2.0

# Ceiling on an honoured crawl-delay. A host asking for an hour between requests has effectively
# declined, and waiting that long inside a collection run would look like a hang. Past this the run
# records the host as unreachable and moves on, which is the honest outcome and a visible one.
MAX_CRAWL_DELAY = 60.0

TIMEOUT = 20.0

# Statuses that mean "you may not have this", as opposed to "this is not here" or "try later".
GATED = {
    401: "authentication wall",
    402: "payment gate",
    403: "access forbidden",
    407: "proxy authentication required",
    451: "unavailable for legal reasons",
}


class Refused(Exception):
    """A source declined to serve this, and the refusal is respected rather than worked around."""

    def __init__(self, url, reason, status=None):
        super().__init__(f"{url}: {reason}")
        self.url = url
        self.reason = reason
        self.status = status

    def as_gap(self):
        """The shape a refusal takes in a run's coverage report."""
        return {"url": self.url, "reason": self.reason, "status": self.status}


def host_of(url):
    parsed = urllib.parse.urlsplit(url)
    return f"{parsed.scheme}://{parsed.netloc}"


def path_of(url):
    parsed = urllib.parse.urlsplit(url)
    return urllib.parse.urlunsplit(("", "", parsed.path or "/", parsed.query, ""))


class RobotsPolicy:
    """What one host's robots.txt permits, fetched once and remembered."""

    def __init__(self, fetch):
        self.fetch = fetch
        self.cache = {}

    def for_host(self, host):
        if host not in self.cache:
            self.cache[host] = self._load(host)

        return self.cache[host]

    def _load(self, host):
        parser = urllib.robotparser.RobotFileParser()

        try:
            status, body = self.fetch(f"{host}/robots.txt")
        except OSError as error:
            # Unknown rules, so nothing is permitted. See rule 2 in the module docstring.
            return _Rules(None, f"robots.txt could not be fetched ({error})")

        if status == 404 or status == 410:
            parser.parse([])
            return _Rules(parser, None)

        if status >= 500:
            return _Rules(None, f"robots.txt returned HTTP {status}")

        if status >= 400:
            # A 4xx that is not "absent" is the host declining to tell us its rules. Treated the same
            # way an unreadable one is, because the position is identical: the rules are unknown.
            return _Rules(None, f"robots.txt returned HTTP {status}")

        parser.parse(body.splitlines())
        return _Rules(parser, None)


class _Rules:
    """Parsed rules, or a stated reason there are none to be had."""

    def __init__(self, parser, unreadable):
        self.parser = parser
        self.unreadable = unreadable

    def allows(self, path):
        if self.unreadable is not None:
            return False

        return self.parser.can_fetch(USER_AGENT, path)

    def delay(self):
        if self.unreadable is not None:
            return None

        published = self.parser.crawl_delay(USER_AGENT)
        return None if published is None else float(published)


class HostLimiter:
    """One host, one request at a time, no faster than agreed."""

    def __init__(self, sleep=time.sleep, now=time.monotonic):
        self.sleep = sleep
        self.now = now
        self.next_allowed = {}

    def wait(self, host, interval):
        due = self.next_allowed.get(host)
        current = self.now()

        if due is not None and due > current:
            self.sleep(due - current)
            current = self.now() + (due - current)

        self.next_allowed[host] = current + interval


class Access:
    """Fetches what a host permits, at the pace it permits, and says so when it permits nothing."""

    def __init__(self, fetch=None, sleep=time.sleep, now=time.monotonic, interval=DEFAULT_INTERVAL):
        self.fetch = fetch or _fetch
        self.robots = RobotsPolicy(self.fetch)
        self.limiter = HostLimiter(sleep, now)
        self.interval = interval
        self.gaps = []

    def get(self, url):
        """Returns the body, or raises Refused. Every refusal is also recorded for the run report."""
        host = host_of(url)
        rules = self.robots.for_host(host)

        if rules.unreadable is not None:
            raise self._record(url, rules.unreadable)

        if not rules.allows(path_of(url)):
            raise self._record(url, "robots.txt disallows this path")

        published = rules.delay()

        if published is not None and published > MAX_CRAWL_DELAY:
            # Effectively a refusal, and recorded as one rather than obeyed into a stall.
            raise self._record(url, f"robots.txt asks for {published:.0f}s between requests")

        self.limiter.wait(host, max(self.interval, published or 0.0))

        try:
            status, body = self.fetch(url)
        except OSError as error:
            raise self._record(url, f"the host could not be reached ({error})") from error

        if status in GATED:
            # Not retried, not worked around, not fetched logged-out through another door. The gate
            # exists to prevent exactly that, and defeating it would make every other guarantee this
            # project makes worth less.
            raise self._record(url, GATED[status], status)

        if status == 429:
            raise self._record(url, "the host asked us to back off", status)

        if status >= 400:
            raise self._record(url, f"HTTP {status}", status)

        return body

    def _record(self, url, reason, status=None):
        refusal = Refused(url, reason, status)
        self.gaps.append(refusal.as_gap())
        return refusal


def _fetch(url):
    """The one function here that touches the network. Everything else decides."""
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})

    try:
        with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
            return response.status, response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", errors="replace")


# --- offline self-test -------------------------------------------------------------------------

def _scripted(responses):
    def fetch(url):
        if url not in responses:
            raise AssertionError(f"unscripted fetch of {url}")

        value = responses[url]

        if isinstance(value, OSError):
            raise value

        return value

    return fetch


def _access(responses, **kwargs):
    slept = []
    clock = [0.0]

    def sleep(seconds):
        slept.append(seconds)
        clock[0] += seconds

    access = Access(_scripted(responses), sleep=sleep, now=lambda: clock[0], **kwargs)
    access.slept = slept
    return access


def self_test():
    checks = []

    def check(name, condition):
        checks.append((name, bool(condition)))

    # 1. A missing robots.txt permits everything.
    access = _access({
        "https://t.me/robots.txt": (404, ""),
        "https://t.me/s/example": (200, "posts"),
    })
    check("absent robots.txt permits the fetch", access.get("https://t.me/s/example") == "posts")
    check("an absent robots.txt is not recorded as a gap", access.gaps == [])

    # 2. An unreachable one forbids everything. The opposite answer to the same missing file, and
    #    the distinction is the whole of rule 2.
    for label, response in (("5xx", (503, "")), ("timeout", OSError("timed out"))):
        access = _access({"https://example.test/robots.txt": response})
        try:
            access.get("https://example.test/page")
            check(f"unreadable robots.txt ({label}) refuses", False)
        except Refused as refusal:
            check(f"unreadable robots.txt ({label}) refuses", "robots.txt" in refusal.reason)
            check(f"unreadable robots.txt ({label}) is recorded as a gap", len(access.gaps) == 1)

    # 3. A disallowed path is refused and an allowed one on the same host is not.
    robots = "User-agent: *\nDisallow: /media_proxy/\nDisallow: /interact/\n"
    access = _access({
        "https://mastodon.example/robots.txt": (200, robots),
        "https://mastodon.example/api/v1/timelines/public": (200, "[]"),
    })
    check("an allowed path is fetched", access.get("https://mastodon.example/api/v1/timelines/public") == "[]")

    try:
        access.get("https://mastodon.example/media_proxy/123")
        check("a disallowed path is refused", False)
    except Refused as refusal:
        check("a disallowed path is refused", "disallows" in refusal.reason)

    # 4. A gate is a refusal and is never retried.
    for status, expected in ((401, "authentication wall"), (402, "payment gate"), (403, "access forbidden")):
        access = _access({
            "https://gated.example/robots.txt": (404, ""),
            "https://gated.example/profile": (status, ""),
        })
        try:
            access.get("https://gated.example/profile")
            check(f"HTTP {status} is a refusal", False)
        except Refused as refusal:
            check(f"HTTP {status} is a refusal", refusal.reason == expected)
            check(f"HTTP {status} is recorded with its status", access.gaps[0]["status"] == status)

    # 5. Requests to one host are spaced, and a published crawl-delay raises the floor.
    access = _access({
        "https://slow.example/robots.txt": (200, "User-agent: *\nCrawl-delay: 5\nAllow: /\n"),
        "https://slow.example/a": (200, "a"),
        "https://slow.example/b": (200, "b"),
    })
    access.get("https://slow.example/a")
    access.get("https://slow.example/b")
    check("a published crawl-delay is honoured", access.slept == [5.0])

    access = _access({
        "https://quick.example/robots.txt": (200, "User-agent: *\nCrawl-delay: 0.1\nAllow: /\n"),
        "https://quick.example/a": (200, "a"),
        "https://quick.example/b": (200, "b"),
    })
    access.get("https://quick.example/a")
    access.get("https://quick.example/b")
    check("a crawl-delay below our own floor does not speed us up", access.slept == [DEFAULT_INTERVAL])

    # 6. An unreasonable crawl-delay is a refusal rather than a stall.
    access = _access({
        "https://glacial.example/robots.txt": (200, f"User-agent: *\nCrawl-delay: {MAX_CRAWL_DELAY + 1:.0f}\n"),
        "https://glacial.example/a": (200, "a"),
    })
    try:
        access.get("https://glacial.example/a")
        check("an unreasonable crawl-delay is refused", False)
    except Refused as refusal:
        check("an unreasonable crawl-delay is refused", "between requests" in refusal.reason)

    # 7. robots.txt is fetched once per host, not once per request.
    fetched = []

    def counting(url):
        fetched.append(url)
        return (404, "") if url.endswith("/robots.txt") else (200, "ok")

    access = Access(counting, sleep=lambda _: None, now=lambda: 0.0)
    access.get("https://once.example/a")
    access.get("https://once.example/b")
    check("robots.txt is fetched once per host", fetched.count("https://once.example/robots.txt") == 1)

    failures = [name for name, passed in checks if not passed]

    for name, passed in checks:
        print(f"{'ok  ' if passed else 'FAIL'} {name}")

    print(f"\n{len(checks) - len(failures)}/{len(checks)} passed")
    return 1 if failures else 0


def main():
    # The whole point of this tooling is text in scripts the console may not be set up for. Windows
    # defaults stdout to the system codepage, and a single emoji in a Bluesky post was enough to
    # crash a fetch that had already succeeded — the failure was in reporting the result, not in
    # getting it, which is the worst place for it because the request had already been made.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--self-test", action="store_true", help="run the offline checks and exit")
    parser.add_argument("url", nargs="?", help="fetch one URL under the access policy")
    arguments = parser.parse_args()

    if arguments.self_test:
        return self_test()

    if not arguments.url:
        parser.print_help()
        return 2

    try:
        print(Access().get(arguments.url))
        return 0
    except Refused as refusal:
        print(json.dumps(refusal.as_gap(), indent=2), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
