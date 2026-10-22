"""Owned target data and first-scan/rescan workflow.

All addresses belong to this object's explicitly registered ctypes buffer. No other
process can be selected, opened, suspended, scanned or written by this module.
The separate test-game process exposes these operations through its own RPC API.
"""
import ctypes
import secrets
import threading
from functools import wraps


def _synchronized(method):
    @wraps(method)
    def guarded(self, *args, **kwargs):
        with self._lock:
            return method(self, *args, **kwargs)
    return guarded


class OwnedLab:
    """A real int32 buffer with two owned resources and deterministic distractors."""
    SIZE = 256
    CURRENCY_INDEX = 37
    AMMO_INDEX = 104
    CURRENCY_LIMIT = 1_000_000
    AMMO_LIMIT = 1_000
    ACTIONS = ("collect", "spend", "fire", "reload")

    def __init__(self):
        self._lock = threading.RLock()
        self._buffer = (ctypes.c_int32 * self.SIZE)()
        self._base = ctypes.addressof(self._buffer)
        self._stride = ctypes.sizeof(ctypes.c_int32)
        self.session = secrets.token_hex(16)
        self.revision = -1
        self.stopped = False
        self.reset()

    def _address(self, index):
        return self._base + index * self._stride

    @property
    def currency_address(self):
        return self._address(self.CURRENCY_INDEX)

    @property
    def ammo_address(self):
        return self._address(self.AMMO_INDEX)

    @_synchronized
    def state(self):
        return {
            "currency": int(self._buffer[self.CURRENCY_INDEX]),
            "ammo": int(self._buffer[self.AMMO_INDEX]),
            "revision": self.revision,
            "session": self.session,
            "stopped": self.stopped,
        }

    @_synchronized
    def reset(self):
        # Filler values are distinct from ordinary demo resources. Some explicit
        # distractors agree initially and for one action, then diverge.
        for i in range(self.SIZE):
            self._buffer[i] = 10_000 + i * 101
        self._buffer[self.CURRENCY_INDEX] = 50
        self._buffer[self.AMMO_INDEX] = 12
        self._buffer[8] = self._buffer[91] = 50
        self._buffer[122] = self._buffer[42] = 12
        self._currency_actions = 0
        self._ammo_actions = 0
        self.revision += 1
        self.stopped = False
        return self.state()

    @_synchronized
    def snapshot(self):
        return {
            "revision": self.revision,
            "session": self.session,
            "cells": [
                {"address": self._address(i), "value": int(value)}
                for i, value in enumerate(self._buffer)
            ],
        }

    @_synchronized
    def perform(self, action, revision=None):
        if revision is not None and (type(revision) is not int or revision != self.revision):
            raise ValueError("Forældet observation; handlingen blev ikke udført")
        if self.stopped:
            raise ValueError("Testprogrammet er stoppet")
        if action not in self.ACTIONS:
            raise ValueError("Ukendt testhandling")
        currency, ammo = self.state()["currency"], self.state()["ammo"]
        if action in ("collect", "spend"):
            value = currency + (25 if action == "collect" else -10)
            if not 0 <= value <= self.CURRENCY_LIMIT:
                raise ValueError("Handlingen ville overskride valutaens grænse")
            self._buffer[self.CURRENCY_INDEX] = value
            self._currency_actions += 1
            self._buffer[8] = value if self._currency_actions == 1 else value + 1
        else:
            value = ammo + (-1 if action == "fire" else 5)
            if not 0 <= value <= self.AMMO_LIMIT:
                raise ValueError("Handlingen ville overskride ammunitionens grænse")
            self._buffer[self.AMMO_INDEX] = value
            self._ammo_actions += 1
            self._buffer[122] = value if self._ammo_actions == 1 else value + 1
        self.revision += 1
        return self.state()

    @_synchronized
    def write(self, address, value, revision, approved=False):
        if self.stopped:
            raise ValueError("Testprogrammet er stoppet")
        if approved is not True:
            raise PermissionError("Skrivning kræver brugerens godkendelse")
        if type(revision) is not int or revision != self.revision:
            raise ValueError("Forældet observation; lav en ny søgning")
        if type(address) is not int or address not in (self.currency_address, self.ammo_address):
            raise ValueError("Adressen er ikke et skrivbart felt i dette testprogram")
        if type(value) is not int:
            raise ValueError("Den nye værdi skal være et heltal")
        limit = self.CURRENCY_LIMIT if address == self.currency_address else self.AMMO_LIMIT
        if not 0 <= value <= limit:
            raise ValueError(f"Den nye værdi skal være mellem 0 og {limit}")
        index = self.CURRENCY_INDEX if address == self.currency_address else self.AMMO_INDEX
        self._buffer[index] = value
        self.revision += 1
        return self.state()

    @_synchronized
    def stop(self):
        # Invalidate every earlier approval even if the value has not changed.
        self.stopped = True
        self.revision += 1
        return self.state()

    @_synchronized
    def resume(self):
        self.stopped = False
        self.revision += 1
        return self.state()


class CandidateScanner:
    """Filter explicitly provided owned-buffer snapshots, never arbitrary RAM."""
    def __init__(self):
        self.candidates = set()
        self.revision = None
        self.value = None
        self.session = None
        self._addresses = set()

    @staticmethod
    def _validate(snapshot, value):
        if type(value) is not int or not -(2 ** 31) <= value < 2 ** 31:
            raise ValueError("Søgeværdien skal være et int32-heltal")
        if not isinstance(snapshot, dict):
            raise ValueError("Ugyldigt snapshot")
        revision, session, cells = snapshot.get("revision"), snapshot.get("session"), snapshot.get("cells")
        if type(revision) is not int or revision < 0 or not isinstance(session, str) or not session:
            raise ValueError("Snapshot mangler gyldig revision eller session")
        if not isinstance(cells, list) or not cells or len(cells) > 100_000:
            raise ValueError("Snapshot har ingen gyldige celler")
        values = {}
        for cell in cells:
            if not isinstance(cell, dict) or set(cell) != {"address", "value"}:
                raise ValueError("Ugyldigt celleformat")
            address, number = cell["address"], cell["value"]
            if type(address) is not int or address <= 0 or address in values:
                raise ValueError("Ugyldig eller gentaget adresse")
            if type(number) is not int or not -(2 ** 31) <= number < 2 ** 31:
                raise ValueError("Ugyldig int32-celle")
            values[address] = number
        return revision, session, values

    def first_scan(self, snapshot, value):
        revision, session, values = self._validate(snapshot, value)
        self.candidates = {address for address, number in values.items() if number == value}
        self._addresses = set(values)
        self.revision, self.session, self.value = revision, session, value
        return sorted(self.candidates)

    def rescan(self, snapshot, value):
        revision, session, values = self._validate(snapshot, value)
        if self.revision is None:
            raise ValueError("Kør en første søgning først")
        if session != self.session or set(values) != self._addresses:
            raise ValueError("Snapshot tilhører et andet testprogram eller buffer")
        if revision <= self.revision:
            raise ValueError("Snapshot er forældet")
        if value == self.value:
            raise ValueError("Værdien skal ændres før næste søgning")
        self.candidates = {address for address in self.candidates if values[address] == value}
        self.revision, self.value = revision, value
        return sorted(self.candidates)

    def unique_address(self):
        if len(self.candidates) != 1:
            raise ValueError("Skrivning kræver præcis én søgekandidat")
        return next(iter(self.candidates))
