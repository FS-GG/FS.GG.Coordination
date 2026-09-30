import os
import pathlib
import subprocess
import tempfile
import textwrap
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[3]
WRAPPER = ROOT / "eng" / "run-choreo-c2-tlc.sh"


class ChoreoC2TlcRetryTests(unittest.TestCase):
    def run_case(self, mode: str):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            command = root / "fake-quint"
            command.write_text(
                textwrap.dedent(
                    """\
                    #!/usr/bin/env bash
                    set -euo pipefail
                    count_file="$FAKE_COUNT_FILE"
                    count=0
                    [[ ! -f "$count_file" ]] || count="$(cat "$count_file")"
                    count=$((count + 1))
                    printf '%s' "$count" > "$count_file"
                    printf '%s\n' "$*" >> "$FAKE_ARGUMENTS_FILE"
                    case "$FAKE_MODE" in
                      retry-success)
                        if [[ "$count" -eq 1 ]]; then
                          echo 'PASS #0: SanyParser'
                          sleep 2
                        else
                          echo '715 states generated, 593 distinct states found, 0 states left on queue.'
                          echo '[ok] No violation found'
                        fi
                        ;;
                      retry-timeout)
                        echo 'PASS #0: SanyParser'
                        sleep 2
                        ;;
                      violation)
                        echo 'Invariant violated'
                        exit 1
                        ;;
                      pre-parser-timeout)
                        sleep 2
                        ;;
                      post-state-timeout)
                        echo 'PASS #0: SanyParser'
                        echo '1 states generated'
                        sleep 2
                        ;;
                    esac
                    """
                ),
                encoding="utf-8",
            )
            command.chmod(0o755)
            result_log = root / "result.log"
            count_file = root / "count"
            arguments_file = root / "arguments"
            environment = os.environ.copy()
            environment.update(
                {
                    "FAKE_MODE": mode,
                    "FAKE_COUNT_FILE": str(count_file),
                    "FAKE_ARGUMENTS_FILE": str(arguments_file),
                }
            )
            result = subprocess.run(
                ["bash", str(WRAPPER), "1", str(result_log), "29820", "--", str(command), "verify"],
                cwd=ROOT,
                env=environment,
                text=True,
                capture_output=True,
                check=False,
            )
            count = int(count_file.read_text(encoding="utf-8"))
            arguments = arguments_file.read_text(encoding="utf-8").splitlines()
            return result, count, arguments, result_log.read_text(encoding="utf-8")

    def test_parser_timeout_retries_once_on_a_fresh_endpoint(self):
        result, count, arguments, log = self.run_case("retry-success")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(2, count)
        self.assertIn("localhost:29820", arguments[0])
        self.assertIn("localhost:29821", arguments[1])
        self.assertIn("[ok] No violation found", log)
        self.assertIn("class=execution-timeout-after-parser", result.stderr)

    def test_second_parser_timeout_fails_closed(self):
        result, count, _, log = self.run_case("retry-timeout")
        self.assertEqual(124, result.returncode)
        self.assertEqual(2, count)
        self.assertIn("PASS #0: SanyParser", log)

    def test_invariant_violation_is_not_retried(self):
        result, count, _, _ = self.run_case("violation")
        self.assertEqual(1, result.returncode)
        self.assertEqual(1, count)

    def test_timeout_before_parser_is_not_retried(self):
        result, count, _, _ = self.run_case("pre-parser-timeout")
        self.assertEqual(124, result.returncode)
        self.assertEqual(1, count)

    def test_timeout_after_state_exploration_begins_is_not_retried(self):
        result, count, _, _ = self.run_case("post-state-timeout")
        self.assertEqual(124, result.returncode)
        self.assertEqual(1, count)


if __name__ == "__main__":
    unittest.main()
