"""Users REST API (v1).

Resource-oriented design: nouns in paths, verbs as HTTP methods, status codes carrying
the outcome. The URL is versioned so the contract can evolve without breaking consumers.
"""

from fastapi import APIRouter, Query, Response, status

from app.api.deps import UserServiceDep
from app.schemas.user import (
    CredentialsCheck,
    CredentialsResult,
    UserCreate,
    UserPage,
    UserRead,
    UserUpdate,
)

router = APIRouter(prefix="/users", tags=["users"])


@router.post(
    "",
    response_model=UserRead,
    status_code=status.HTTP_201_CREATED,
    summary="Register a user",
    responses={409: {"description": "Email already registered"}},
)
def create_user(payload: UserCreate, service: UserServiceDep) -> UserRead:
    return UserRead.model_validate(service.register(payload))


@router.get("", response_model=UserPage, summary="List users")
def list_users(
    service: UserServiceDep,
    limit: int = Query(default=20, ge=1, le=100),
    offset: int = Query(default=0, ge=0),
) -> UserPage:
    users, total = service.list(limit=limit, offset=offset)
    return UserPage(
        items=[UserRead.model_validate(user) for user in users],
        total=total,
        limit=limit,
        offset=offset,
    )


@router.get(
    "/{user_id}",
    response_model=UserRead,
    summary="Get a user by id",
    responses={404: {"description": "User not found"}},
)
def get_user(user_id: str, service: UserServiceDep) -> UserRead:
    return UserRead.model_validate(service.get(user_id))


@router.patch(
    "/{user_id}",
    response_model=UserRead,
    summary="Update a user",
    responses={404: {"description": "User not found"}},
)
def update_user(user_id: str, payload: UserUpdate, service: UserServiceDep) -> UserRead:
    return UserRead.model_validate(service.update(user_id, payload))


@router.delete(
    "/{user_id}",
    status_code=status.HTTP_204_NO_CONTENT,
    summary="Delete a user",
    responses={404: {"description": "User not found"}},
)
def delete_user(user_id: str, service: UserServiceDep) -> Response:
    service.delete(user_id)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.post(
    "/credentials:verify",
    response_model=CredentialsResult,
    summary="Verify a user's credentials",
    description=(
        "Internal endpoint for the Auth Service. Returns the same shape for an unknown "
        "email and a wrong password so callers cannot enumerate accounts."
    ),
)
def verify_credentials(payload: CredentialsCheck, service: UserServiceDep) -> CredentialsResult:
    user = service.verify_credentials(payload.email, payload.password)
    if user is None:
        return CredentialsResult(valid=False)
    return CredentialsResult(valid=True, user=UserRead.model_validate(user))
