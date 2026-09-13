#!/usr/bin/env python3
"""Read Tier B open social under the access policy and write a collection bundle.

    python tools/collect/collect.py --brief maritime-chokepoints --out data/osint/<date>-<brief>.json
    python tools/collect/collect.py --self-test

The application never invokes this. A bundle is a committed file that can be reviewed in a diff,
replayed, and re-verified long after the run that produced it — see docs/adr/025-agent-collected-osint.md
for why collection is a separate act from ingestion, and docs/adr/030-collection-access-policy.md for
what this is and is not allowed to do to obtain one.

What this may write is bounded by the bundle schema rather than by this file's good intentions: there
is no field for a coordinate, an event type or a severity, so a run that was somehow persuaded by a
page it was reading still cannot express one. What it produces is a citation.

The three platforms are the ones the plan verified serve public content without authentication:

- Telegram, via the server-rendered t.me/s/<channel> preview.
- Bluesky, via the public AppView. Author feeds only; searchPosts answers 403, so collection is by
  curated account list rather than by search, and that is a limitation rather than a preference.
- Mastodon, via an instance's public account timeline.
"""

import argparse
import datetime as dt
import hashlib
import html
import html.parser
import json
import os
import re
import sys
import unicodedata

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from access import Access, Refused  # noqa: E402

SCHEMA_VERSION = 1
MAX_EXCERPT = 1000
MAX_PLACE_NAMES = 8

GAZETTEER = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "src", "Geopolitics.Infrastructure", "Location", "Data", "theatre-places.json",
)

# The same rule the runtime gazetteer applies, and it is duplicated here on purpose rather than
# skipped. See ShortNameLength and ShortNamePopulationFloor in Gazetteer.cs: the extract contains
# villages genuinely named Sad, Rama, Gora and Bile, and aliases including Luck and Mare, so hunting
# for short names in prose costs precision. A collector that recorded "Luck" as a place name every
# time a post said "a stroke of luck" would be feeding the pipeline the same bug through a new door.
SHORT_NAME_LENGTH = 4
SHORT_NAME_POPULATION_FLOOR = 20_000


def normalise(text):
    """NFC, control characters stripped, whitespace collapsed — before anything is hashed or stored.

    Bidi overrides go because they can make a string render as something other than what it says,
    which matters acutely in a field quoting right-to-left text. The zero-width non-joiner stays: it
    looks like the same class of character and is not, being letter-affecting in Persian, and
    removing it turns ordinary words into misspellings.
    """
    text = unicodedata.normalize("NFC", text)
    kept = []

    for character in text:
        if character == "‌":
            kept.append(character)
            continue

        if unicodedata.category(character) in {"Cc", "Cf"}:
            continue

        kept.append(character)

    return re.sub(r"\s+", " ", "".join(kept)).strip()


def excerpt_of(text):
    """A contiguous verbatim quotation, cut at a word boundary rather than mid-character.

    Measured in text elements, not UTF-16 units. A cap written for English and counted in units cuts
    Arabic and Han text at a fraction of its stated limit, and the whole point of this tier is
    non-English collection.
    """
    text = normalise(text)

    if len(text) <= MAX_EXCERPT:
        return text

    cut = text[:MAX_EXCERPT]
    space = cut.rfind(" ")
    return (cut[:space] if space > MAX_EXCERPT // 2 else cut).rstrip() + "…"


def content_hash(excerpt):
    return "sha256:" + hashlib.sha256(excerpt.encode("utf-8")).hexdigest()


def load_lexicon(path=GAZETTEER):
    """Names worth looking for in prose, from the committed extract.

    Matching rather than inferring. "The place names appearing verbatim in the text" is a substring
    question against a known lexicon, and answering it that way keeps the collector inside its remit:
    it reports what it read, and the pipeline's own gazetteer decides what that means.
    """
    with open(path, encoding="utf-8") as handle:
        document = json.load(handle)

    # The committed extract wraps its rows in metadata recording where they came from; a bare list is
    # what the self-test writes. Accepting both keeps the test from having to reproduce the header.
    places = document["places"] if isinstance(document, dict) else document
    names = set()

    for place in places:
        population = place.get("population") or 0

        for index, name in enumerate([place["name"], *place.get("aliases", [])]):
            preferred = index == 0

            if len(name) > SHORT_NAME_LENGTH or (preferred and population >= SHORT_NAME_POPULATION_FLOOR):
                names.add(name)

    return sorted(names, key=len, reverse=True)


def matches_terms(text, terms):
    """Whether this post is about what the brief asked for.

    Searching in English finds what English publishes, so a brief carries the same concept in every
    language it covers and a post qualifies by matching any one of them. Without this a run against
    general-interest channels would collect whatever those channels happened to post, file it under a
    brief about maritime chokepoints, and the bundle would misrepresent what was looked for — which
    is the failure the brief exists to prevent.

    No terms at all means no filter, for a brief that genuinely wants everything a channel says.
    """
    if not terms:
        return True

    folded = text.casefold()
    return any(term.casefold() in folded for term in terms)


def place_names(text, lexicon):
    """Every lexicon name the text actually contains, longest first, capped."""
    folded = text.casefold()
    found = []

    for name in lexicon:
        if len(found) >= MAX_PLACE_NAMES:
            break

        if name.casefold() in folded and name not in found:
            found.append(name)

    return found


class _TelegramParser(html.parser.HTMLParser):
    """Pulls post id, text and timestamp out of the server-rendered preview page.

    Written against the markup rather than with a dependency, because this tool has to run from a
    clone with nothing installed — the same constraint the dashboard test runner accepted. The
    markup is the contract and it is pinned by the self-test below, so a change to it fails here
    rather than silently producing an empty run.
    """

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.posts = []
        self.current = None
        self.depth = 0
        self.in_text = 0

    def handle_starttag(self, tag, attrs):
        attributes = dict(attrs)
        classes = attributes.get("class", "").split()

        if "tgme_widget_message" in classes and attributes.get("data-post"):
            self.current = {"id": attributes["data-post"], "text": [], "time": None}
            self.depth = 0

        if self.current is None:
            return

        if tag == "div":
            self.depth += 1

        if "tgme_widget_message_text" in classes:
            self.in_text = self.depth

        if tag == "time" and attributes.get("datetime") and self.current["time"] is None:
            self.current["time"] = attributes["datetime"]

        if tag == "br" and self.in_text:
            self.current["text"].append(" ")

    def handle_endtag(self, tag):
        if self.current is None or tag != "div":
            return

        if self.in_text and self.depth == self.in_text:
            self.in_text = 0

        self.depth -= 1

        if self.depth <= 0:
            if self.current["text"] and self.current["time"]:
                self.posts.append(self.current)

            self.current = None

    def handle_data(self, data):
        if self.current is not None and self.in_text:
            self.current["text"].append(data)


def read_telegram(access, channel, limit, retrieved_at, lexicon):
    body = access.get(f"https://t.me/s/{channel}")
    parser = _TelegramParser()
    parser.feed(body)

    items = []

    for post in reversed(parser.posts):
        text = excerpt_of("".join(post["text"]))

        if not text:
            # A post that is only an image or a video carries no quotation, and an item with nothing
            # to quote is not a citation. Skipped rather than recorded with an empty excerpt.
            continue

        items.append(_item(
            platform="telegram",
            channel=channel,
            url=f"https://t.me/{post['id']}",
            posted_at=post["time"],
            excerpt=text,
            retrieved_at=retrieved_at,
            lexicon=lexicon,
        ))

        if len(items) >= limit:
            break

    return items


def read_bluesky(access, handle, limit, retrieved_at, lexicon):
    body = access.get(
        "https://public.api.bsky.app/xrpc/app.bsky.feed.getAuthorFeed"
        f"?actor={handle}&limit={min(limit * 2, 100)}&filter=posts_no_replies"
    )
    feed = json.loads(body).get("feed", [])
    items = []

    for entry in feed:
        post = entry.get("post", {})
        record = post.get("record", {})
        text = excerpt_of(record.get("text", ""))

        if not text or "reason" in entry:
            # "reason" marks a repost. A repost is the same claim travelling, not a second witness,
            # and recording one as an item from this channel would let a single account corroborate
            # itself by amplification.
            continue

        rkey = post.get("uri", "").rsplit("/", 1)[-1]

        items.append(_item(
            platform="bluesky",
            channel=handle,
            url=f"https://bsky.app/profile/{handle}/post/{rkey}",
            posted_at=record.get("createdAt"),
            excerpt=text,
            retrieved_at=retrieved_at,
            lexicon=lexicon,
            language=(record.get("langs") or [None])[0],
        ))

        if len(items) >= limit:
            break

    return items


def read_mastodon(access, instance, handle, limit, retrieved_at, lexicon):
    account = json.loads(access.get(f"https://{instance}/api/v1/accounts/lookup?acct={handle}"))
    statuses = json.loads(access.get(
        f"https://{instance}/api/v1/accounts/{account['id']}/statuses"
        f"?limit={min(limit * 2, 40)}&exclude_replies=true&exclude_reblogs=true"
    ))

    items = []

    for status in statuses:
        text = excerpt_of(_strip_tags(status.get("content", "")))

        if not text or status.get("reblog"):
            continue

        items.append(_item(
            platform="mastodon",
            channel=f"{handle}@{instance}",
            url=status.get("url") or status.get("uri"),
            posted_at=status.get("created_at"),
            excerpt=text,
            retrieved_at=retrieved_at,
            lexicon=lexicon,
            language=status.get("language"),
        ))

        if len(items) >= limit:
            break

    return items


def _strip_tags(markup):
    """Mastodon serves post bodies as HTML. Paragraph and line breaks become spaces, not nothing."""
    spaced = re.sub(r"(?i)<(br\s*/?|/p|/div)>", " ", markup)
    return html.unescape(re.sub(r"<[^>]+>", "", spaced))


def _item(platform, channel, url, posted_at, excerpt, retrieved_at, lexicon, language=None):
    item = {
        "kind": "userGenerated",
        "url": url,
        "platform": platform,
        "channel": channel,
        "postedAt": _instant(posted_at),
        "retrievedAt": retrieved_at,
        "contentHash": content_hash(excerpt),
        "excerpt": excerpt,
        "placeNames": place_names(excerpt, lexicon),
        "relatedTo": [],
    }

    if language:
        item["language"] = language

    return item


def _instant(value):
    """Whatever the platform published, expressed the one way the schema reads."""
    parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    return parsed.astimezone(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def apply_caps(items, per_channel, per_platform, total):
    """The brief's source diversity limits, enforced here rather than trusted to a prompt.

    A prolific channel must not be able to dominate the picture simply by posting more, and one busy
    platform must not be able to stand in for the world. Both caps are the brief's; this only applies
    them, and it reports what it dropped rather than trimming silently — a run that quietly discarded
    half of what it found would read as a thin day rather than as a cap doing its job.
    """
    kept = []
    dropped = []
    by_channel = {}
    by_platform = {}
    seen = set()

    for item in items:
        channel = f"{item['platform']}/{item['channel']}"

        if item["url"] in seen:
            dropped.append((item["url"], "already collected in this run"))
            continue

        if by_channel.get(channel, 0) >= per_channel:
            dropped.append((item["url"], f"channel cap of {per_channel}"))
            continue

        if by_platform.get(item["platform"], 0) >= per_platform:
            dropped.append((item["url"], f"platform cap of {per_platform}"))
            continue

        if len(kept) >= total:
            dropped.append((item["url"], f"run cap of {total}"))
            continue

        seen.add(item["url"])
        by_channel[channel] = by_channel.get(channel, 0) + 1
        by_platform[item["platform"]] = by_platform.get(item["platform"], 0) + 1
        kept.append(item)

    return kept, dropped


def build_bundle(brief_id, revision, items, collected_at, outcomes=None):
    """The run, and the run's own account of itself.

    The coverage block is not decoration. Without it a channel that refused, a channel that published
    nothing, and a channel that was read and said nothing relevant all reduce to the same thing —
    an absence — and an absence reads as "nothing happened there" rather than as "we did not see".
    Recording it in the bundle rather than in a log is what lets the dashboard publish the count
    alongside the claim, which is the only honest way to present breadth.
    """
    bundle = {
        "schemaVersion": SCHEMA_VERSION,
        "bundleId": f"{collected_at[:16].replace(':', '')}Z-{brief_id}",
        "collectedAt": collected_at,
        "brief": {"id": brief_id, "revision": revision},
        "collector": {"role": "agent", "runId": hashlib.sha256(collected_at.encode()).hexdigest()[:8]},
        "items": items,
    }

    if outcomes:
        bundle["coverage"] = {"sources": outcomes}

    return bundle


# A source is read generously and filtered afterwards, so that "nothing matched" is measurable. This
# is how many posts are pulled from one channel before the brief's terms are applied.
READ_DEPTH = 40


def run(plan, access=None, now=None):
    """Reads every source the run names and reports, per source, what came of it.

    The per-source outcome is the point as much as the items are. A channel that could not be read
    and a channel that was read and said nothing relevant are different facts, and a bundle that
    recorded only the items would make them look identical — an empty space, which is the one thing
    the coverage measurement exists to stop this project presenting.
    """
    access = access or Access()
    collected_at = (now or dt.datetime.now(dt.timezone.utc)).strftime("%Y-%m-%dT%H:%M:%SZ")
    lexicon = load_lexicon()
    terms = plan.get("terms") or []
    caps = plan.get("caps") or {}
    found = []
    outcomes = []

    for source in plan["sources"]:
        platform = source["platform"]
        channel = f"{platform}/{source['channel']}"

        try:
            if platform == "telegram":
                read = read_telegram(access, source["channel"], READ_DEPTH, collected_at, lexicon)
            elif platform == "bluesky":
                read = read_bluesky(access, source["channel"], READ_DEPTH, collected_at, lexicon)
            elif platform == "mastodon":
                read = read_mastodon(
                    access, source["instance"], source["channel"], READ_DEPTH, collected_at, lexicon)
            else:
                raise ValueError(f"'{platform}' is not a Tier B platform this reads")
        except Refused as refusal:
            # A refusal is a coverage gap, not an error to retry around. The run continues and says
            # what it could not reach, because a gap that is recorded is a known limitation and a gap
            # that is not is a false claim of coverage.
            outcomes.append({"channel": channel, "outcome": "unreachable", "reason": refusal.reason})
            continue

        if not read:
            # Three outcomes, not two, because they are three different facts. A refusal is above.
            # This is a source that answered and carried nothing readable: a Telegram channel with no
            # public preview redirects to its join page, which parses cleanly and contains no posts,
            # and an account can exist on Bluesky while publishing none — bbcnews.bsky.social does,
            # verified 2026-09-13. Neither is a quiet week, and both would look like one without
            # this.
            outcomes.append({"channel": channel, "outcome": "no public posts", "reason": "served nothing readable"})
            continue

        relevant = [item for item in read if matches_terms(item["excerpt"], terms)]
        outcomes.append({
            "channel": channel,
            "outcome": "collected" if relevant else "nothing matched",
            "read": len(read),
            "matched": len(relevant),
        })
        found += relevant

    kept, dropped = apply_caps(
        found,
        caps.get("perChannel", 3),
        caps.get("perPlatform", 8),
        caps.get("total", 40),
    )

    for url, reason in dropped:
        print(f"dropped ({reason}): {url}", file=sys.stderr)

    # A source's "matched" count is what the brief's terms accepted, before the diversity caps were
    # applied across the run. The two differ whenever a cap binds, and the bundle records both rather
    # than quietly reporting the smaller one: how much a channel had to say and how much of it this
    # run was willing to take are different facts.
    kept_by_channel = {}

    for item in kept:
        channel = f"{item['platform']}/{item['channel']}"
        kept_by_channel[channel] = kept_by_channel.get(channel, 0) + 1

    for outcome in outcomes:
        if outcome["outcome"] in {"unreachable", "no public posts"}:
            continue

        outcome["collected"] = kept_by_channel.get(outcome["channel"], 0)

        # A channel that had something to say and had all of it taken away by a cap is not a channel
        # that was collected, and it is emphatically not a quiet one. Naming the state is the whole
        # point: the run took nothing from here because it had already taken enough from this
        # platform, which is a fact about this run rather than about the channel.
        if outcome["matched"] > 0 and outcome["collected"] == 0:
            outcome["outcome"] = "capped"

    return build_bundle(plan["brief"], plan["revision"], kept, collected_at, outcomes), outcomes, access.gaps


# --- offline self-test -------------------------------------------------------------------------

TELEGRAM_SAMPLE = """
<div class="tgme_widget_message_wrap"><div class="tgme_widget_message js-widget_message" data-post="example/12">
  <div class="tgme_widget_message_bubble">
    <div class="tgme_widget_message_text js-message_text" dir="auto">Повідомляють про вибухи у місті
      <a href="?q=%23Kharkiv">#Kharkiv</a>.<br/>Деталі уточнюються.</div>
    <div class="tgme_widget_message_footer"><time datetime="2026-09-12T19:05:00+00:00">19:05</time></div>
  </div>
</div></div>
<div class="tgme_widget_message_wrap"><div class="tgme_widget_message js-widget_message" data-post="example/13">
  <div class="tgme_widget_message_bubble">
    <div class="tgme_widget_message_text js-message_text" dir="auto">Second post, in English, naming Odesa.</div>
    <div class="tgme_widget_message_footer"><time datetime="2026-09-12T20:10:00+00:00">20:10</time></div>
  </div>
</div></div>
"""


def self_test():
    checks = []

    def check(name, condition):
        checks.append((name, bool(condition)))

    lexicon = ["Kharkiv", "Odesa", "Харків"]
    retrieved = "2026-09-12T21:00:00Z"

    # 1. Telegram markup yields post id, text and time, oldest first.
    access = _stub({"https://t.me/s/example": TELEGRAM_SAMPLE})
    items = read_telegram(access, "example", 10, retrieved, lexicon)

    check("both telegram posts are read", len(items) == 2)

    # Newest first. The preview page serves oldest at the top, and taking the first N off the page
    # would have collected the channel's oldest posts inside its window rather than its latest.
    check("the newest post is first", items[0]["url"] == "https://t.me/example/13")

    oldest = items[1]
    check("the post url is the canonical one", oldest["url"] == "https://t.me/example/12")
    check("a line break becomes a space, not nothing", "Kharkiv .Деталі" not in oldest["excerpt"])
    check("the quotation is verbatim", "Повідомляють про вибухи" in oldest["excerpt"])
    check("the timestamp is normalised to UTC", oldest["postedAt"] == "2026-09-12T19:05:00Z")
    check("place names found in the text are recorded", oldest["placeNames"] == ["Kharkiv"])
    check("a second post records its own places", items[0]["placeNames"] == ["Odesa"])
    check("no item carries a coordinate", all("latitude" not in item for item in items))
    check("every item is user-generated", all(item["kind"] == "userGenerated" for item in items))

    # 2. The hash is over the excerpt as recorded, so an edited quotation is detectable offline.
    check(
        "the hash covers the excerpt exactly",
        items[0]["contentHash"] == content_hash(items[0]["excerpt"]),
    )
    check("an edited quotation changes the hash", content_hash("a") != content_hash("a "))

    # 3. Bluesky reposts are not a second witness.
    feed = {
        "feed": [
            {"post": {"uri": "at://x/app.bsky.feed.post/aaa", "record": {
                "text": "Shelling reported near Odesa.", "createdAt": "2026-09-12T18:00:00.000Z", "langs": ["en"]}}},
            {"reason": {"$type": "app.bsky.feed.defs#reasonRepost"},
             "post": {"uri": "at://x/app.bsky.feed.post/bbb", "record": {
                 "text": "Someone else's post.", "createdAt": "2026-09-12T18:30:00.000Z"}}},
        ]
    }
    access = _stub({
        "https://public.api.bsky.app/xrpc/app.bsky.feed.getAuthorFeed"
        "?actor=example.bsky.social&limit=20&filter=posts_no_replies": json.dumps(feed)
    })
    items = read_bluesky(access, "example.bsky.social", 10, retrieved, lexicon)

    check("a repost is not collected as this channel's own item", len(items) == 1)
    check("the bluesky url is rebuilt from the record key", items[0]["url"].endswith("/post/aaa"))
    check("the declared language is carried", items[0]["language"] == "en")

    # 4. Mastodon HTML bodies become plain text.
    check(
        "mastodon markup becomes readable text",
        _strip_tags("<p>Line one.</p><p>Line two.</p>").split() == ["Line", "one.", "Line", "two."],
    )

    # 5. Excerpts are bounded and measured in characters, not bytes.
    long_arabic = "غارة " * 400
    check("a long excerpt is cut", len(excerpt_of(long_arabic)) <= MAX_EXCERPT + 1)
    check("the cut is marked", excerpt_of(long_arabic).endswith("…"))

    # 6. Bidi overrides go and the zero-width non-joiner stays.
    check("bidi overrides are stripped", "‮" not in normalise("safe‮text"))
    check("the persian zero-width non-joiner survives", "‌" in normalise("می‌رود"))

    # 7. Diversity caps, and what they dropped is reported rather than silent.
    many = [
        _item("telegram", "a", f"https://t.me/a/{n}", "2026-09-12T10:00:00Z", f"Post {n}", retrieved, lexicon)
        for n in range(5)
    ] + [
        _item("bluesky", "b", f"https://bsky.app/profile/b/post/{n}", "2026-09-12T10:00:00Z", f"Post {n}", retrieved, lexicon)
        for n in range(5)
    ]
    kept, dropped = apply_caps(many, per_channel=2, per_platform=3, total=10)

    check("the per-channel cap holds", len(kept) == 4)
    check("every drop states its reason", len(dropped) == 6 and all(reason for _, reason in dropped))

    kept, dropped = apply_caps(many, per_channel=4, per_platform=4, total=5)
    check("the run cap holds", len(kept) == 5)

    duplicated = many[:1] * 2
    kept, dropped = apply_caps(duplicated, per_channel=9, per_platform=9, total=9)
    check("one url is collected once", len(kept) == 1)

    # 8. The short-name rule, which exists because of a real regression.
    extract = [
        {"name": "Sad", "population": 300, "aliases": []},
        {"name": "Kyiv", "population": 2_900_000, "aliases": ["Київ"]},
        {"name": "Kharkiv", "population": 1_400_000, "aliases": []},
    ]
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_selftest-lexicon.json")

    with open(path, "w", encoding="utf-8") as handle:
        json.dump(extract, handle)

    try:
        names = load_lexicon(path)
    finally:
        os.remove(path)

    check("a hamlet named Sad is not hunted for in prose", "Sad" not in names)
    check("Kyiv is, because it is a city", "Kyiv" in names)
    check("longer names need no population", "Kharkiv" in names)

    # 9. The brief's terms decide what a run keeps, in whichever language they are written.
    check("an english term matches", matches_terms("Shelling near the strait.", ["strait", "مضيق"]))
    check("an arabic term matches the same concept", matches_terms("إطلاق نار قرب مضيق باب المندب", ["strait", "مضيق"]))
    check("an unrelated post does not match", not matches_terms("Our design contest winners.", ["strait", "مضيق"]))
    check("a brief with no terms filters nothing", matches_terms("Anything at all.", []))

    # 10. Read-but-nothing-matched and could-not-be-read are different outcomes.
    plan = {
        "brief": "test", "revision": 1, "terms": ["strait"],
        "sources": [{"platform": "telegram", "channel": "quiet"}, {"platform": "telegram", "channel": "closed"}],
    }
    access = _stub({
        "https://t.me/s/quiet": TELEGRAM_SAMPLE,
        "https://t.me/s/closed": "<html><body>Please join to view.</body></html>",
    })
    bundle, outcomes, _ = run(plan, access=access, now=dt.datetime(2026, 9, 12, 21, 0, tzinfo=dt.timezone.utc))

    capped, _ = apply_caps(many, per_channel=9, per_platform=1, total=9)
    check("a cap can take everything a channel offered", len(capped) == 2)

    check("the run records its own coverage in the bundle", "coverage" in bundle)
    check("every source appears in it", len(bundle["coverage"]["sources"]) == 2)
    check("a channel read that matched nothing says so", outcomes[0]["outcome"] == "nothing matched")
    check("it still records how much it read", outcomes[0]["read"] == 2)
    check("a channel serving no posts is not a quiet channel", outcomes[1]["outcome"] == "no public posts")
    check("neither contributes items", bundle["items"] == [])

    failures = [name for name, passed in checks if not passed]

    for name, passed in checks:
        print(f"{'ok  ' if passed else 'FAIL'} {name}")

    print(f"\n{len(checks) - len(failures)}/{len(checks)} passed")
    return 1 if failures else 0


class _stub:
    def __init__(self, responses):
        self.responses = responses
        self.gaps = []

    def get(self, url):
        if url not in self.responses:
            raise AssertionError(f"unscripted fetch of {url}")

        return self.responses[url]


def main():
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--self-test", action="store_true", help="run the offline checks and exit")
    parser.add_argument("--run", dest="plan", help="the committed run file: brief, terms, caps, sources")
    parser.add_argument("--out", help="where to write the bundle; stdout when absent")
    arguments = parser.parse_args()

    if arguments.self_test:
        return self_test()

    if not arguments.plan:
        parser.print_help()
        return 2

    with open(arguments.plan, encoding="utf-8") as handle:
        plan = json.load(handle)

    bundle, outcomes, gaps = run(plan)
    rendered = json.dumps(bundle, ensure_ascii=False, indent=2) + "\n"

    if arguments.out:
        with open(arguments.out, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(rendered)

        print(f"{len(bundle['items'])} item(s) written to {arguments.out}", file=sys.stderr)
    else:
        print(rendered)

    for outcome in outcomes:
        detail = outcome.get("reason") or f"{outcome.get('matched', 0)} of {outcome.get('read', 0)} matched"
        print(f"{outcome['outcome']:>15}: {outcome['channel']} — {detail}", file=sys.stderr)

    for gap in gaps:
        print(f"            gap: {gap['url']} — {gap['reason']}", file=sys.stderr)

    return 0


if __name__ == "__main__":
    sys.exit(main())
