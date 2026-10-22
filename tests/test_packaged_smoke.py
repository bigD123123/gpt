import unittest

from vision_agent.packaged_smoke import run


class FakeClient:
    def __init__(self, state=None, snapshot=None):
        self.state = state or {
            "currency": 50, "ammo": 12, "revision": 0,
            "session": "owned-session", "window_handle": 123,
        }
        self.snapshot = snapshot or {
            "session": "owned-session",
            "cells": [{"address": index + 1, "value": index} for index in range(256)],
        }
        self.closed = False

    def request(self, command):
        return self.state if command == "state" else self.snapshot

    def close(self):
        self.closed = True


class PackagedSmokeTests(unittest.TestCase):
    def test_accepts_complete_owned_target_and_always_closes(self):
        client = FakeClient()
        self.assertEqual(run(lambda: client), 0)
        self.assertTrue(client.closed)

    def test_rejects_wrong_initial_state_and_closes(self):
        client = FakeClient(state={
            "currency": 51, "ammo": 12, "revision": 0,
            "session": "owned-session", "window_handle": 123,
        })
        with self.assertRaises(RuntimeError):
            run(lambda: client)
        self.assertTrue(client.closed)
