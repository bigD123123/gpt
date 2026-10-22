"""Minimal packaged-process acceptance check used by the Windows build.

This opens only the test program bundled in the same executable, checks its
documented RPC boundary and closes it. It does not call a model or write a
final value.
"""
from .replica_client import LabClient


def run(client_factory=LabClient):
    client = client_factory()
    try:
        state = client.request("state")
        required = {"currency", "ammo", "revision", "session", "window_handle"}
        if not required.issubset(state):
            raise RuntimeError("Det pakkede testprogram returnerede ikke en komplet tilstand")
        if state["currency"] != 50 or state["ammo"] != 12:
            raise RuntimeError("Det pakkede testprogram startede med uventede værdier")
        snapshot = client.request("snapshot")
        if snapshot.get("session") != state["session"] or len(snapshot.get("cells", [])) != 256:
            raise RuntimeError("Det pakkede testprogram returnerede ikke sin registrerede buffer")
        return 0
    finally:
        client.close()
