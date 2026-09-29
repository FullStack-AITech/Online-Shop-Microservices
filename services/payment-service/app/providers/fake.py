"""A deterministic stand-in for a card processor.

Deterministic on purpose: the same amount always gets the same answer, so tests and the
end-to-end smoke test can force a decline just by ordering something expensive.
"""

import hashlib
from decimal import Decimal

from app.providers.base import ProviderResult

DECLINE_CODE = "card_declined"


class FakePaymentProvider:
    def __init__(self, decline_above: Decimal) -> None:
        self._decline_above = decline_above

    def charge(self, *, amount: Decimal, currency: str, reference: str) -> ProviderResult:
        if amount > self._decline_above:
            return ProviderResult(approved=False, decline_code=DECLINE_CODE)
        # Derived from the reference, like a real provider's idempotent charge: charging
        # the same reference twice returns the same transaction.
        digest = hashlib.sha256(reference.encode("utf-8")).hexdigest()[:16]
        return ProviderResult(approved=True, provider_reference=f"fake_{digest}")
