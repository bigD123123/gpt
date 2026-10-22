import json
import unittest
from dataclasses import FrozenInstanceError
from unittest.mock import patch

from vision_agent.replica_ai import (
    SCHEMA,
    ReplicaAIError,
    ReplicaDecision,
    UsageMetadata,
    _response_text,
    analyze,
)


def decision(**changes):
    data = {"stat_type": "currency", "observed_value": 50, "action": "observe", "reason": "Aflæst fra testbilledet"}
    data.update(changes)
    return json.dumps(data)


def completed(raw=None, usage=None):
    response = {"status": "completed", "output": [
        {"type": "reasoning", "summary": []},
        {"type": "message", "status": "completed", "content": [
            {"type": "output_text", "text": decision() if raw is None else raw}
        ]}
    ]}
    if usage is not None:
        response["usage"] = usage
    return response


class ReplicaDecisionTests(unittest.TestCase):
    def test_initial_and_locked_verification(self):
        parsed = ReplicaDecision.parse(decision(), "initial")
        self.assertEqual(parsed.stat_type, "currency")
        self.assertEqual(parsed.usage, UsageMetadata())
        self.assertEqual(parsed.model_dict(), json.loads(decision()))
        self.assertEqual(set(parsed.model_dict()), set(SCHEMA["required"]))
        self.assertNotIn("usage", parsed.model_dict())
        self.assertEqual(ReplicaDecision.parse(decision(), "verify", "currency").observed_value, 50)

    def test_usage_metadata_is_immutable(self):
        usage = UsageMetadata(provider="openai", input_tokens=3, output_tokens=2, total_tokens=5)
        with self.assertRaises(FrozenInstanceError):
            usage.total_tokens = 9

    def test_change_actions_match_stat(self):
        for stat, action, value in (("currency", "collect", 50), ("currency", "spend", 50),
                                    ("ammo", "fire", 12), ("ammo", "reload", 12)):
            with self.subTest(action=action):
                parsed = ReplicaDecision.parse(decision(stat_type=stat, action=action, observed_value=value), "change", stat)
                self.assertEqual(parsed.action, action)

    def test_invalid_model_outputs_are_rejected(self):
        invalid = ["not json", "[]", decision(extra="ignored"), decision(observed_value=True),
                   decision(observed_value=50.0), decision(observed_value=-1), decision(observed_value=1000001),
                   decision(stat_type="health"), decision(action="shell"), decision(reason=" "),
                   decision(reason="x" * 1001), decision(stat_type="ammo", observed_value=1001),
                   '{"stat_type":"ammo","stat_type":"currency","observed_value":50,"action":"observe","reason":"x"}']
        for raw in invalid:
            with self.subTest(raw=raw), self.assertRaises(ReplicaAIError):
                ReplicaDecision.parse(raw, "initial")

    def test_wrong_phase_action_and_changed_stat_are_rejected(self):
        cases = [(decision(action="collect"), "initial", None),
                 (decision(action="collect"), "verify", "currency"),
                 (decision(action="observe"), "change", "currency"),
                 (decision(action="finish"), "change", "currency"),
                 (decision(action="fire"), "change", "currency"),
                 (decision(stat_type="ammo", observed_value=12), "verify", "currency")]
        for raw, phase, stat in cases:
            with self.subTest(phase=phase, stat=stat), self.assertRaises(ReplicaAIError):
                ReplicaDecision.parse(raw, phase, stat)

    def test_impossible_changes_are_rejected(self):
        for stat, action, value in (("currency", "spend", 9), ("currency", "collect", 1000000),
                                   ("currency", "collect", 999976), ("currency", "collect", 999990),
                                   ("ammo", "fire", 0), ("ammo", "reload", 1000), ("ammo", "reload", 996),
                                   ("ammo", "reload", 998)):
            with self.subTest(action=action), self.assertRaises(ReplicaAIError):
                ReplicaDecision.parse(decision(stat_type=stat, action=action, observed_value=value), "change", stat)

    def test_exact_increment_limits_execute_in_owned_lab(self):
        from vision_agent.replica_lab import OwnedLab
        for stat, value, action, expected in (("currency", 999975, "collect", 1000000),
                                               ("ammo", 995, "reload", 1000)):
            with self.subTest(stat=stat):
                lab = OwnedLab()
                address = lab.currency_address if stat == "currency" else lab.ammo_address
                state = lab.write(address, value, lab.revision, approved=True)
                parsed = ReplicaDecision.parse(decision(stat_type=stat, action=action, observed_value=value), "change", stat)
                after = lab.perform(parsed.action, state["revision"])
                self.assertEqual(after[stat], expected)

    def test_context_must_be_known_before_call(self):
        for phase, stat in (("unknown", None), ("change", None), ("verify", None), ("initial", "health")):
            with self.subTest(phase=phase), self.assertRaises(ReplicaAIError):
                analyze("Demo (ingen AI)", "", b"", phase, stat, {"currency": 50})


class ReplicaResponseTests(unittest.TestCase):
    def test_completed_response_extracts_only_message(self):
        self.assertEqual(_response_text(completed()), decision())

    def test_partial_refused_empty_and_malformed_are_rejected(self):
        invalid = [None, {"status": "incomplete", "output": []}, {"status": "completed", "output": []},
                   {"status": "completed", "output": [None]},
                   {"status": "completed", "output": [{"type": "message", "content": [
                       {"type": "refusal", "refusal": "do not echo raw content"}]}]},
                   {"status": "completed", "output": [{"type": "message", "status": "incomplete", "content": []}]}]
        for response in invalid:
            with self.subTest(response=response), self.assertRaises(ReplicaAIError):
                _response_text(response)

    @patch("vision_agent.replica_ai.api_key", return_value="test-only-key")
    @patch("vision_agent.replica_ai._post")
    def test_responses_image_payload_and_schema(self, post, key):
        post.return_value = completed(usage={"input_tokens": 321, "output_tokens": 17, "total_tokens": 338})
        result = analyze("OpenAI", "gpt-5-nano", b"png bytes", "initial", demo_state={"currency": 98765})
        self.assertEqual(result.observed_value, 50)
        self.assertEqual(result.usage, UsageMetadata("openai", 321, 17, 338))
        url, payload, auth = post.call_args.args
        self.assertEqual(url, "https://api.openai.com/v1/responses")
        self.assertEqual(auth, "test-only-key")
        self.assertEqual(payload["reasoning"], {"effort": "minimal"})
        self.assertEqual(payload["max_output_tokens"], 2048)
        self.assertFalse(payload["store"])
        self.assertTrue(payload["text"]["format"]["strict"])
        self.assertTrue(payload["input"][0]["content"][1]["image_url"].startswith("data:image/png;base64,"))
        self.assertNotIn("tools", payload)
        self.assertNotIn("98765", json.dumps(payload))
        self.assertNotIn("usage", payload["text"]["format"]["schema"]["properties"])

    @patch("vision_agent.replica_ai.api_key", return_value="test-only-key")
    @patch("vision_agent.replica_ai._post")
    def test_openai_unknown_or_malformed_usage_is_zero(self, post, key):
        malformed = [None, [], "usage", {"input_tokens": True, "output_tokens": -1, "total_tokens": 3.0}]
        for usage in malformed:
            with self.subTest(usage=usage):
                post.return_value = completed()
                if usage is not None:
                    post.return_value["usage"] = usage
                result = analyze("OpenAI", "gpt-5-nano", b"png", "initial")
                self.assertEqual(result.usage, UsageMetadata(provider="openai"))

        post.return_value = completed(usage={"input_tokens": 9, "output_tokens": "4", "total_tokens": -1})
        result = analyze("OpenAI", "gpt-5-nano", b"png", "initial")
        self.assertEqual(result.usage, UsageMetadata(provider="openai", input_tokens=9))

    @patch("vision_agent.replica_ai.api_key", return_value="test-only-key")
    @patch("vision_agent.replica_ai._post")
    def test_other_model_generations_use_default_reasoning(self, post, key):
        post.return_value = completed()
        for model in ("gpt-4.1-mini", "gpt-5.1", "gpt-5.2"):
            with self.subTest(model=model):
                analyze("OpenAI", model, b"png", "initial")
                self.assertNotIn("reasoning", post.call_args.args[1])

    @patch("vision_agent.replica_ai.api_key", return_value=None)
    @patch("vision_agent.replica_ai._post")
    def test_missing_key_makes_no_request(self, post, key):
        with self.assertRaises(ReplicaAIError):
            analyze("OpenAI", "gpt-5-nano", b"png", "initial")
        post.assert_not_called()

    @patch("vision_agent.replica_ai.api_key", return_value="test-only-key")
    @patch("vision_agent.replica_ai._post", side_effect=TimeoutError())
    def test_timeout_has_no_retry_or_demo_fallback(self, post, key):
        with self.assertRaises(TimeoutError):
            analyze("OpenAI", "gpt-5-nano", b"png", "initial")
        self.assertEqual(post.call_count, 1)

    @patch("vision_agent.replica_ai._post")
    def test_local_vision_payload_and_decision(self, post):
        post.return_value = {"done": True, "done_reason": "stop", "message": {
            "content": decision(stat_type="ammo", observed_value=12, action="fire")},
            "prompt_eval_count": 72, "eval_count": 8}
        result = analyze("Ollama (lokal)", "llama3.2-vision", b"png", "change", "ammo")
        self.assertEqual(result.action, "fire")
        self.assertEqual(result.usage, UsageMetadata("ollama", 72, 8, 80))
        url, payload = post.call_args.args
        self.assertEqual(url, "http://127.0.0.1:11434/api/chat")
        self.assertTrue(payload["messages"][1]["images"])
        self.assertFalse(payload["stream"])

    @patch("vision_agent.replica_ai._post")
    def test_local_malformed_usage_is_safely_normalized(self, post):
        post.return_value = {"done": True, "message": {"content": decision()},
                             "prompt_eval_count": True, "eval_count": -4}
        result = analyze("Ollama (lokal)", "model", b"png", "initial")
        self.assertEqual(result.usage, UsageMetadata(provider="ollama"))

    @patch("vision_agent.replica_ai._post")
    def test_local_partial_response_is_rejected(self, post):
        for response in ({"done": False, "message": {"content": decision()}},
                         {"done": True, "done_reason": "length", "message": {"content": decision()}},
                         {"done": True, "message": {"content": 3}}):
            post.return_value = response
            with self.subTest(response=response), self.assertRaises(ReplicaAIError):
                analyze("Ollama (lokal)", "model", b"png", "initial")

    def test_demo_phases_and_limits_without_network(self):
        state = {"currency": 50, "ammo": 12}
        with patch("vision_agent.replica_ai._post") as post:
            initial = analyze("Demo (ingen AI)", "", b"", "initial", demo_state=state)
            self.assertEqual(initial.action, "observe")
            self.assertEqual(initial.usage, UsageMetadata(provider="demo"))
            self.assertEqual(analyze("Demo (ingen AI)", "", b"", "change", "currency", state).action, "collect")
            self.assertEqual(analyze("Demo (ingen AI)", "", b"", "verify", "currency", state).action, "observe")
            self.assertEqual(analyze("Demo (ingen AI)", "", b"", "change", "ammo", state).action, "fire")
            self.assertEqual(analyze("Demo (ingen AI)", "", b"", "change", "ammo", {"ammo": 0}).action, "reload")
            self.assertEqual(analyze("Demo (ingen AI)", "", b"", "change", "currency", {"currency": 1000000}).action, "spend")
            post.assert_not_called()

    def test_demo_changes_are_accepted_near_owned_lab_limits(self):
        from vision_agent.replica_lab import OwnedLab
        for stat, value in (("currency", 999976), ("currency", 999990),
                            ("currency", 1000000), ("ammo", 996), ("ammo", 998), ("ammo", 1000)):
            with self.subTest(stat=stat, value=value):
                lab = OwnedLab()
                address = lab.currency_address if stat == "currency" else lab.ammo_address
                state = lab.write(address, value, lab.revision, approved=True)
                parsed = analyze("Demo (ingen AI)", "", b"", "change", stat, state)
                self.assertEqual(parsed.action, "spend" if stat == "currency" else "fire")
                after = lab.perform(parsed.action, state["revision"])
                self.assertNotEqual(after[stat], value)

    @patch("vision_agent.replica_ai._post")
    def test_bad_configuration_makes_no_network_request(self, post):
        for provider, model, png in (("unknown", "model", b"png"), ("OpenAI", "", b"png"),
                                     ("OpenAI", "gpt-5-nano", b"")):
            with self.subTest(provider=provider), self.assertRaises(ReplicaAIError):
                analyze(provider, model, png, "initial")
        post.assert_not_called()


if __name__ == "__main__":
    unittest.main()
