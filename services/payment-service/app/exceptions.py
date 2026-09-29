"""Domain errors.

The domain layer raises these; the API layer is the only place that knows about HTTP
status codes. That keeps the service layer reusable from the event consumer, which turns
the same errors into retries or a dead letter instead.
"""


class DomainError(Exception):
    """Base class for expected, business-rule failures."""


class PaymentNotFoundError(DomainError):
    def __init__(self, payment_id: str) -> None:
        super().__init__(f"Payment '{payment_id}' was not found")
        self.payment_id = payment_id


class InvalidPaymentError(DomainError):
    """A payment that could never be valid: a non-positive amount or a bad currency."""


class IllegalPaymentTransitionError(DomainError):
    def __init__(self, current: str, target: str) -> None:
        super().__init__(f"A payment cannot move from {current} to {target}")
        self.current = current
        self.target = target


class IdempotencyKeyReusedError(DomainError):
    def __init__(self, idempotency_key: str) -> None:
        super().__init__(
            f"Idempotency key '{idempotency_key}' was already used for a different payment"
        )
        self.idempotency_key = idempotency_key
