#!/usr/bin/env bash
#
# Everything CI checks, in one command, so a failure is found before it is pushed.
#
# This file is the single copy of those checks: .github/workflows/ci.yml calls it stage by stage
# rather than repeating them. That matters more than the convenience. When the drift check lived only
# in the workflow, the only way to discover it was to push and wait for the email — and four of the
# eight CI failures in the week this was written were exactly that, a change that altered the
# evaluation figures with the regenerated RESULTS.md left uncommitted.
#
#   bash tools/verify.sh            # everything, in CI's order
#   bash tools/verify.sh figures    # one stage
#
# Runs from anywhere; it locates the repository itself. Stops at the first failure and says what to
# do about it. Docker is the one check that cannot be run everywhere, and its absence is reported
# rather than skipped silently.

set -euo pipefail

cd "$(dirname "$0")/.."

SOLUTION="GeopoliticsDashboard.sln"
CONFIGURATION="${CONFIGURATION:-Release}"
REPORTS="tests/data/ai-evaluation/RESULTS.md tests/data/severity-model/RESULTS.md"

step() { printf '\n\033[1m==> %s\033[0m\n' "$1"; }
fail() { printf '\n\033[31merror: %s\033[0m\n' "$1" >&2; exit 1; }

stage_restore() {
    step "Restore"
    dotnet restore "$SOLUTION"
}

stage_build() {
    step "Build ($CONFIGURATION)"
    dotnet build "$SOLUTION" --configuration "$CONFIGURATION" --no-restore
}

# Has to run before `figures` below, because the evaluation suites rewrite their own RESULTS.md as
# they go. After this stage those files hold what the current code actually produces.
stage_test() {
    step "Test"
    dotnet test "$SOLUTION" --configuration "$CONFIGURATION" --no-build
}

# The dashboard client is JavaScript and is not in the .NET solution. No install step: the suite has
# no dependencies, which is the point of it. See docs/adr/024-dashboard-test-runner.md.
stage_dashboard() {
    step "Test the dashboard client"
    node --version
    npm test
}

stage_figures() {
    step "Published evaluation figures must match what the code produces"

    local drift
    drift=$(git diff -U0 -- $REPORTS | grep -E '^[+-][^+-]' | grep -v 'Latency median' || true)

    if [ -n "$drift" ]; then
        git --no-pager diff -- $REPORTS
        fail "Committed evaluation results do not match this code.
The suite has already regenerated them, so the fix is to commit the files above:
    git add $REPORTS
Latency is excluded from this check because it is wall-clock time; every other figure in
those reports is deterministic, which is why the rest can be enforced."
    fi

    # The README quotes several of these figures so a reader does not have to open another file.
    # That copy is where drift hides next, so every row it shares verbatim has to agree.
    local quoted
    quoted=$(grep -cE '^\| (Structured output success rate|Language accuracy|Location name|Entities) ' README.md || true)

    if [ "$quoted" -lt 4 ]; then
        fail "The README no longer quotes the evaluation rows this check compares. Update the check
or restore the rows; do not let it pass by matching nothing."
    fi

    while IFS= read -r row; do
        grep -Fqx "$row" tests/data/ai-evaluation/RESULTS.md \
            || fail "README quotes an evaluation figure the harness did not produce: $row"
    done < <(grep -E '^\| (Structured output success rate|Language accuracy|Location name|Entities) ' README.md)

    printf 'Committed figures agree with this code.\n'
}

# The collection tooling is Python and is in neither suite. What it decides is the access policy —
# whether a path is allowed, whether a status is a refusal, how long to wait — which is exactly the
# kind of rule that gets stated in a document, believed, and is quietly untrue a year later. Both
# self-tests run offline. See docs/adr/030-collection-access-policy.md.
stage_tooling() {
    step "Test the collection tooling"

    local python
    python=$(command -v python3 || command -v python) || fail "No Python interpreter was found."
    "$python" --version
    "$python" tools/collect/access.py --self-test
    "$python" tools/collect/collect.py --self-test

    # What an unattended round does with what it collected, which is a third set of rules of the
    # same kind: an empty bundle from a quiet week and an empty bundle from a collector that reached
    # nothing are the same file and opposite facts. See docs/adr/038-collection-without-being-asked.md.
    "$python" tools/collect/schedule.py --self-test

    # The round itself, syntax-checked. It runs unattended on a schedule, so the first time anyone
    # reads its output is after it has already decided what to commit.
    bash -n tools/collect.sh
}

stage_format() {
    step "Format"
    dotnet format "$SOLUTION" --verify-no-changes --no-restore
}

stage_docker() {
    step "Container build"

    if [ "${SKIP_DOCKER:-}" = "1" ]; then
        printf 'Skipped: SKIP_DOCKER=1.\n'
        return
    fi

    if command -v docker >/dev/null 2>&1; then
        docker build -t geopolitics-dashboard .
        return
    fi

    # Stated rather than passed over. A check that quietly does nothing on the machine where the
    # change is written, and then fails on the machine that publishes it, is the failure mode this
    # script exists to remove — and the Dockerfile has broken that way before.
    printf '\033[33mNot run: docker is not installed here, so CI is the first place this is checked.\n'
    printf 'A change touching Dockerfile, the solution layout, or a project file is unverified\n'
    printf 'until the push lands.\033[0m\n'
}

ALL_STAGES="restore build test dashboard figures tooling format docker"

run_stage() {
    case " $ALL_STAGES " in
        *" $1 "*) "stage_$1" ;;
        *) fail "Unknown stage '$1'. Available: $ALL_STAGES" ;;
    esac
}

if [ "$#" -eq 0 ] || [ "$1" = "all" ]; then
    for stage in $ALL_STAGES; do
        run_stage "$stage"
    done

    printf '\n\033[32mAll checks passed.\033[0m\n'
else
    for stage in "$@"; do
        run_stage "$stage"
    done
fi
