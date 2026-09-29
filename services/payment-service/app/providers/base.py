"""The seam between this service and whoever actually moves the money.

A Protocol rather than a base class: a real provider's client only has to have the right
shape, and tests can pass any object with a ``charge`` method.
"""

from dataclasses import dataclass
from decimal import Decimal
from typing import Protocol


@dataclass(frozen=True)
class ProviderResult:
    approved: bool
    provider_reference: str | None = None
    # A machine code such as ``card_declined``; travels as PaymentFailed's ``reason``.
    decline_code: str | None = None


class PaymentProvider(Protocol):
    def charge(self, *, amount: Decimal, currency: str, reference: str) -> ProviderResult:
        """Charge once per ``reference``.

        Real providers (Stripe, Adyen) deduplicate on a caller-supplied key. Passing the
        payment's idempotency key as ``reference`` is what makes it safe to resume a
        payment that crashed mid-charge.
        """
        ...
