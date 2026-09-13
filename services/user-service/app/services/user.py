"""Business rules for users.

Everything that is true of a user regardless of *how* the request arrived lives here:
uniqueness of email, password hashing, activation state.
"""

from sqlalchemy.exc import IntegrityError

from app.core.config import Settings
from app.core.security import hash_password, verify_password
from app.exceptions import EmailAlreadyRegisteredError, UserNotFoundError
from app.models.user import User
from app.repositories.user import UserRepository
from app.schemas.user import UserCreate, UserUpdate

MAX_PAGE_SIZE = 100


class UserService:
    def __init__(self, repository: UserRepository, settings: Settings) -> None:
        self._repository = repository
        self._settings = settings

    def register(self, payload: UserCreate) -> User:
        email = _normalise_email(payload.email)
        if self._repository.get_by_email(email) is not None:
            raise EmailAlreadyRegisteredError(email)

        user = User(
            email=email,
            full_name=payload.full_name.strip(),
            password_hash=hash_password(
                payload.password, iterations=self._settings.password_hash_iterations
            ),
        )
        try:
            return self._repository.add(user)
        except IntegrityError as exc:
            # Two concurrent registrations can both pass the check above; the unique
            # index is the real guard, so translate its violation into the same error.
            raise EmailAlreadyRegisteredError(email) from exc

    def get(self, user_id: str) -> User:
        user = self._repository.get(user_id)
        if user is None:
            raise UserNotFoundError(user_id)
        return user

    def list(self, *, limit: int, offset: int) -> tuple[list[User], int]:
        limit = min(max(limit, 1), MAX_PAGE_SIZE)
        offset = max(offset, 0)
        return self._repository.list(limit=limit, offset=offset), self._repository.count()

    def update(self, user_id: str, payload: UserUpdate) -> User:
        user = self.get(user_id)
        changes = payload.model_dump(exclude_unset=True)
        if "full_name" in changes and changes["full_name"] is not None:
            user.full_name = changes["full_name"].strip()
        if "is_active" in changes and changes["is_active"] is not None:
            user.is_active = changes["is_active"]
        return self._repository.save(user)

    def delete(self, user_id: str) -> None:
        self._repository.delete(self.get(user_id))

    def verify_credentials(self, email: str, password: str) -> User | None:
        """Return the user when the credentials are valid, otherwise ``None``.

        Callers must not distinguish "unknown email" from "wrong password" in their
        responses, so this collapses both cases into ``None``. Inactive users cannot log in.
        """
        user = self._repository.get_by_email(_normalise_email(email))
        if user is None or not user.is_active:
            return None
        if not verify_password(password, user.password_hash):
            return None
        return user


def _normalise_email(email: str) -> str:
    """Emails are matched case-insensitively, so store them folded."""
    return email.strip().lower()
