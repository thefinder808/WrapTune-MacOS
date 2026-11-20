#!/usr/bin/env bash
#
# Run a command so that a hang ends with evidence instead of a silent stall.
#
#   watchdog.sh <limit-seconds> <command> [args...]
#
# Why this exists: twice on 2026-11-20 the CI Test step wedged for 20+ minutes.
# Neither --blame-hang (4m) nor the step's own timeout (8m) ended it, cancelling
# took five more minutes, and GitHub kept no log for either run — so there was
# nothing to diagnose. Two things here are aimed at exactly that:
#
#   1. The command's output goes to a file, not to this step's output pipe. A
#      step can't finish while anything still holds its pipe open, and a stray
#      child process that outlives the tests would hold it; a file can't be
#      held hostage that way.
#   2. If the command is still running at the limit, this prints the process
#      list and everything the command wrote so far, kills it, and fails the
#      step — while the runner can still upload the log.
#
# Exit status: the command's own, or 124 if it was killed at the limit.

set -u

limit="$1"
shift
log="${WATCHDOG_LOG:-test-output.log}"

# PIDs alive before the command starts, to spot anything it leaves behind.
pids_before="$(ps -axo pid= 2>/dev/null | tr -d ' ' | sort -u)"

"$@" > "$log" 2>&1 < /dev/null &
pid=$!

# Depth-first, so children are gone before the parent that would reap them.
kill_tree() {
  local child
  for child in $(pgrep -P "$1" 2>/dev/null); do
    kill_tree "$child"
  done
  kill -9 "$1" 2>/dev/null || true
}

while kill -0 "$pid" 2>/dev/null; do
  if [ "$SECONDS" -ge "$limit" ]; then
    echo "::error::'$*' was still running after ${limit}s and was killed. The process list and its output so far are below."
    echo "::group::Processes when it was killed"
    ps -axo pid,ppid,etime,stat,command 2>/dev/null || ps -ef || true
    echo "::endgroup::"
    echo "::group::Output so far"
    cat "$log" 2>/dev/null || true
    echo "::endgroup::"
    kill_tree "$pid"
    exit 124
  fi
  sleep 2
done

status=0
wait "$pid" || status=$?
cat "$log"

# With the output in a file, a process the command left running no longer
# stalls the step — which also means it would go unnoticed. So name any
# process that started during the run and is still alive. Informational: on a
# shared runner an unrelated daemon can show up here too.
leftover="$(ps -axo pid=,etime=,command= 2>/dev/null |
  awk -v self="$$" 'NR == FNR { before[$1]; next } !($1 in before) && $1 != self' \
    <(printf '%s\n' "$pids_before") - |
  grep -v -E '(ps -axo pid=|awk -v self=)' || true)"
if [ -n "$leftover" ]; then
  echo "::group::Processes started during the run that are still alive"
  echo "$leftover"
  echo "::endgroup::"
fi

exit "$status"
