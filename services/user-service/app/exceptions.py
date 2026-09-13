"""Domain errors.

The domain layer raises these; the API layer is the only place that knows about HTTP
status codes. That keeps the service layer reusable from a future gRPC or event handler.
"""


class DomainError(Exception):
    """Base class for expected, business-rule failures."""


class UserNotFoundError(DomainError):
    def __init__(self, user_id: str) -> None:
        super().__init__(f"User '{user_id}' was not found")
        self.user_id = user_id


class EmailAlreadyRegisteredError(DomainError):
    def __init__(self, email: str) -> None:
        super().__init__(f"Email '{email}' is already registered")
        self.email = email
