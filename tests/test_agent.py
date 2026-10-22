import json
import unittest
from unittest.mock import patch
from vision_agent.core import Decision, TestLab
from vision_agent.providers import decide


def response(**changes):
    data = dict(observed_value=20, action="add", amount=10, reason="Mod målet")
    data.update(changes)
    return json.dumps(data)


class BoundaryTests(unittest.TestCase):
    def setUp(self):
        self.lab = TestLab()

    def test_valid_action_and_goal_verification(self):
        result = self.lab.apply(Decision.parse(response()), 0, 30)
        self.assertEqual(result, dict(before=20, after=30, goal_reached=True))

    def test_untrusted_model_outputs(self):
        invalid = ["not json", "[]", response(action="shell"), response(amount=11),
                   response(amount=0), response(amount=True), response(observed_value="20"),
                   response(observed_value=101), response(reason=""), response(extra="x"),
                   response(action="finish", amount=1), response(action="set", amount=101)]
        for raw in invalid:
            with self.subTest(raw=raw), self.assertRaises((ValueError, TypeError)):
                Decision.parse(raw)

    def test_stale_revision_is_blocked(self):
        self.lab.reset(20)
        with self.assertRaisesRegex(ValueError, "forældet"):
            self.lab.apply(Decision.parse(response()), 0, 30)
        self.assertEqual(self.lab.value, 20)

    def test_wrong_visual_reading_is_blocked(self):
        with self.assertRaises(ValueError):
            self.lab.apply(Decision.parse(response(observed_value=19)), 0, 30)
        self.assertEqual(self.lab.value, 20)

    def test_sensitive_action_requires_approval(self):
        decision = Decision.parse(response(action="set", amount=70))
        with self.assertRaises(PermissionError):
            self.lab.apply(decision, 0, 70)
        self.assertEqual(self.lab.value, 20)
        self.assertTrue(self.lab.apply(decision, 0, 70, True)["goal_reached"])
        with self.assertRaises(ValueError):
            self.lab.apply(decision, 0, 70, True)

    def test_stop_blocks_even_approved_action(self):
        self.lab.stop()
        with self.assertRaises(ValueError):
            self.lab.apply(Decision.parse(response(action="set", amount=70)), 0, 70, True)
        self.assertEqual(self.lab.value, 20)

    def test_no_premature_finish(self):
        with self.assertRaises(ValueError):
            self.lab.apply(Decision.parse(response(action="finish", amount=0)), 0, 70)

    def test_range_is_checked_before_mutation(self):
        self.lab.reset(99)
        with self.assertRaises(ValueError):
            self.lab.apply(Decision.parse(response(observed_value=99)), 1, 100)
        self.assertEqual(self.lab.value, 99)

    def test_parser_cannot_be_bypassed(self):
        with self.assertRaises(ValueError):
            self.lab.apply(Decision(20, "shell", 10, "bad"), 0, 30)

    def test_demo_complete_loop_up_down_and_already_done(self):
        for target in (0, 20, 73, 100):
            lab = TestLab()
            for _ in range(12):
                decision = decide("Demo (ingen AI)", "", b"", target, lab.value)
                result = lab.apply(decision, lab.revision, target)
                if result["goal_reached"]:
                    break
            self.assertEqual(lab.value, target)


class ProviderTests(unittest.TestCase):
    @patch("vision_agent.providers._post")
    @patch("vision_agent.providers.api_key", return_value="test-not-real")
    def test_openai_sends_image_and_parses_decision(self, key, post):
        post.return_value = {"choices": [{"message": {"content": response()}}]}
        decision = decide("OpenAI", "gpt-4.1-mini", b"png bytes", 70)
        self.assertEqual(decision.amount, 10)
        url, payload, _ = post.call_args.args
        self.assertEqual(url, "https://api.openai.com/v1/chat/completions")
        self.assertTrue(payload["response_format"]["json_schema"]["strict"])
        self.assertTrue(payload["messages"][1]["content"][1]["image_url"]["url"].startswith("data:image/png;base64,"))
        self.assertNotIn("tools", payload)

    @patch("vision_agent.providers._post")
    def test_local_vision_model(self, post):
        post.return_value = {"message": {"content": response(action="set", amount=70)}}
        self.assertEqual(decide("Ollama (lokal)", "llama3.2-vision", b"png", 70).action, "set")
        self.assertEqual(post.call_args.args[0], "http://127.0.0.1:11434/api/chat")
        self.assertTrue(post.call_args.args[1]["messages"][1]["images"])

    @patch("vision_agent.providers.api_key", return_value=None)
    @patch("vision_agent.providers._post")
    def test_no_key_no_request(self, post, key):
        with self.assertRaises(ValueError):
            decide("OpenAI", "model", b"png", 70)
        post.assert_not_called()

    @patch("vision_agent.providers._post", side_effect=TimeoutError())
    @patch("vision_agent.providers.api_key", return_value="test-not-real")
    def test_no_hidden_fallback_or_retry(self, key, post):
        with self.assertRaises(TimeoutError):
            decide("OpenAI", "model", b"png", 70)
        self.assertEqual(post.call_count, 1)

    @patch("vision_agent.providers._post")
    def test_malformed_local_output_is_rejected(self, post):
        post.return_value = {"message": {"content": response(action="keyboard")}}
        with self.assertRaises(ValueError):
            decide("Ollama (lokal)", "model", b"png", 70)


if __name__ == "__main__":
    unittest.main()
