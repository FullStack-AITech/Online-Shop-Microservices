"""Data access for the User aggregate.

The repository is the only layer that talks SQL. Swapping the storage engine should not
require touching the service or API layers.
"""

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.models.user import User


class UserRepository:
    def __init__(self, session: Session) -> None:
        self._session = session

    def add(self, user: User) -> User:
        self._session.add(user)
        self._session.commit()
        self._session.refresh(user)
        return user

    def get(self, user_id: str) -> User | None:
        return self._session.get(User, user_id)

    def get_by_email(self, email: str) -> User | None:
        stmt = select(User).where(User.email == email)
        return self._session.execute(stmt).scalar_one_or_none()

    def list(self, *, limit: int, offset: int) -> list[User]:
        stmt = select(User).order_by(User.created_at.desc()).limit(limit).offset(offset)
        return list(self._session.execute(stmt).scalars())

    def count(self) -> int:
        return int(self._session.execute(select(func.count()).select_from(User)).scalar_one())

    def save(self, user: User) -> User:
        self._session.commit()
        self._session.refresh(user)
        return user

    def delete(self, user: User) -> None:
        self._session.delete(user)
        self._session.commit()
