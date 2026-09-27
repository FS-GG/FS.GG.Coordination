"""Pure request-envelope negatives; never construct a provider transport."""

import dataclasses
import pathlib
import sys
import unittest
from unittest import mock

ENG = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ENG))
sys.path.insert(0, str(ENG / "tests/callable-isolated-v2-native-request-custody"))
from test_native_request_custody import fixture, selected_target, NOW
import callable_isolated_v2_native_request_custody as custody
import callable_isolated_v2_provider_request_spec as provider


def matching():
    verified, event, intent, native = fixture()
    joined = custody.qualify(verified, event, intent, native,
                             selected_target(), 600, "e" * 64,
                             4, "9" * 40, NOW)
    return joined, verified, native


class ProviderRequestSpecTests(unittest.TestCase):
    def test_exact_request_has_closed_fixed_transport_policy(self):
        joined, verified, native = matching()
        result = provider.prepare(joined, verified, native,
                                  "https://api.github.com")
        self.assertEqual(result.method, "POST")
        self.assertEqual(result.url,
                         "https://api.github.com/repos/FS-GG/v2-synthetic/pulls")
        self.assertEqual(result.body, verified.canonical_request)
        self.assertEqual(result.max_sends, 1)
        self.assertEqual(result.automatic_retries, 0)
        self.assertFalse(result.follow_redirects)
        self.assertFalse(result.authorized)
        self.assertFalse(result.can_dispatch)
        self.assertEqual(result.live_effects, 0)

    def test_host_alias_port_query_fragment_or_userinfo_refuses(self):
        joined, verified, native = matching()
        for origin in ("http://api.github.com", "https://API.GITHUB.COM",
                       "https://api.github.com:443", "https://api.github.com/",
                       "https://api.github.com?x=1",
                       "https://api.github.com#fragment",
                       "https://user@api.github.com",
                       "https://api.github.com.evil", ""):
            with self.subTest(origin=origin), self.assertRaises(provider.Refused):
                provider.prepare(joined, verified, native, origin)

    def test_changed_join_or_native_body_refuses(self):
        joined, verified, native = matching()
        for change in (("join", "operation_id", "c" * 64),
                       ("join", "request_sha256", "c" * 64),
                       ("native", "canonical_request", b"{}"),
                       ("native", "request_sha256", "c" * 64),
                       ("native", "method", "PUT"),
                       ("native", "max_provider_writes", 2),
                       ("native", "path", "repos/FS-GG/foreign/pulls")):
            side, field, value = change
            with self.subTest(change=change), self.assertRaises(provider.Refused):
                new_join = dataclasses.replace(joined, **{field: value}) if side == "join" else joined
                new_native = dataclasses.replace(native, **{field: value}) if side == "native" else native
                provider.prepare(new_join, verified, new_native,
                                 "https://api.github.com")

    def test_foreign_path_suffix_and_untrusted_decision_refuse(self):
        joined, verified, native = matching()
        for path in ("repos/FS-GG/v2-synthetic/pulls?x=1",
                     "repos/FS-GG/v2-synthetic/pulls/",
                     "repos/FS-GG/v2-synthetic/pulls#x",
                     "//evil.example/pulls"):
            with self.subTest(path=path), self.assertRaises(provider.Refused):
                provider.prepare(joined, verified,
                    dataclasses.replace(native, path=path),
                    "https://api.github.com")
        with self.assertRaises(provider.Refused):
            provider.prepare(object(), verified, native,
                             "https://api.github.com")

    def test_result_cannot_be_replaced_as_authority(self):
        joined, verified, native = matching()
        result = provider.prepare(joined, verified, native,
                                  "https://api.github.com")
        for field, value in (("authorized", True), ("can_dispatch", True),
                             ("live_effects", 1), ("max_sends", 2),
                             ("automatic_retries", 1),
                             ("follow_redirects", True)):
            with self.subTest(field=field):
                with self.assertRaises((TypeError, ValueError)):
                    dataclasses.replace(result, **{field: value})

    def test_no_token_socket_file_or_journal_access(self):
        joined, verified, native = matching()
        with mock.patch("builtins.open", side_effect=AssertionError("file")), \
             mock.patch("os.getenv", side_effect=AssertionError("token")), \
             mock.patch("socket.socket", side_effect=AssertionError("socket")), \
             mock.patch("sqlite3.connect", side_effect=AssertionError("sqlite")):
            result = provider.prepare(joined, verified, native,
                                      "https://api.github.com")
        self.assertFalse(result.can_dispatch)


if __name__ == "__main__":
    unittest.main()
