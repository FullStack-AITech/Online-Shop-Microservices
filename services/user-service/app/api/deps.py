"""Shared FastAPI dependencies — the wiring between HTTP and the domain."""

from typing import Annotated

from fastapi import Depends
from sqlalchemy.orm import Session

from app.core.config import Settings, get_settings
from app.db.session import get_session
from app.repositories.user import UserRepository
from app.services.user import UserService

SessionDep = Annotated[Session, Depends(get_session)]
SettingsDep = Annotated[Settings, Depends(get_settings)]


def get_user_service(session: SessionDep, settings: SettingsDep) -> UserService:
    return UserService(UserRepository(session), settings)


UserServiceDep = Annotated[UserService, Depends(get_user_service)]
