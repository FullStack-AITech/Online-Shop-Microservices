"""Request and response contracts for the Users API.

These are deliberately separate from the ORM model: the wire contract is a public
interface other services depend on, while the table is a private implementation detail.
"""

from datetime import datetime

from pydantic import BaseModel, ConfigDict, EmailStr, Field


class UserCreate(BaseModel):
    email: EmailStr
    full_name: str = Field(min_length=1, max_length=200)
    password: str = Field(min_length=8, max_length=128)


class UserUpdate(BaseModel):
    """Partial update — only the supplied fields are changed."""

    full_name: str | None = Field(default=None, min_length=1, max_length=200)
    is_active: bool | None = None


class UserRead(BaseModel):
    """Public view of a user. Never exposes the password hash."""

    model_config = ConfigDict(from_attributes=True)

    id: str
    email: EmailStr
    full_name: str
    is_active: bool
    created_at: datetime
    updated_at: datetime


class UserPage(BaseModel):
    """Offset-paginated list response."""

    items: list[UserRead]
    total: int
    limit: int
    offset: int


class CredentialsCheck(BaseModel):
    """Used by the Auth Service to validate a login without ever reading the hash."""

    email: EmailStr
    password: str


class CredentialsResult(BaseModel):
    valid: bool
    user: UserRead | None = None
