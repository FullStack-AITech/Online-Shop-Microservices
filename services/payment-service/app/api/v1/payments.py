"""Payments REST API (v1).

Taking a payment is not naturally idempotent — two identical POSTs are two charges — so
the client names the attempt with an ``Idempotency-Key`` header and a retry with the same
key gets the first answer back instead of a second charge.
"""

from typing import Annotated

from fastapi import APIRouter, Header, Query, Response, status

from app.api.deps import PaymentServiceDep
from app.schemas.payment import PaymentCreate, PaymentPage, PaymentRead

router = APIRouter(prefix="/payments", tags=["payments"])

IdempotencyKey = Annotated[
    str,
    Header(
        alias="Idempotency-Key",
        min_length=1,
        max_length=100,
        description="Client-chosen key for this payment attempt. Reuse it to retry safely.",
    ),
]


@router.post(
    "",
    response_model=PaymentRead,
    status_code=status.HTTP_201_CREATED,
    summary="Take a payment",
    responses={
        200: {"description": "Repeat of an earlier request with this key; the same payment"},
        409: {"description": "`idempotency_key_reused` — the key was used for another payment"},
        422: {"description": "Missing `Idempotency-Key` header or invalid body"},
    },
)
def create_payment(
    payload: PaymentCreate,
    idempotency_key: IdempotencyKey,
    service: PaymentServiceDep,
    response: Response,
) -> PaymentRead:
    payment, created = service.process(
        idempotency_key=idempotency_key,
        order_id=payload.order_id,
        user_id=payload.user_id,
        amount=payload.amount,
        currency=payload.currency,
    )
    if not created:
        # Same body as the first response; the status tells the client it was a replay.
        response.status_code = status.HTTP_200_OK
    return PaymentRead.model_validate(payment)


@router.get("", response_model=PaymentPage, summary="List payments")
def list_payments(
    service: PaymentServiceDep,
    order_id: str | None = Query(default=None, max_length=36),
    limit: int = Query(default=20, ge=1, le=100),
    offset: int = Query(default=0, ge=0),
) -> PaymentPage:
    payments, total = service.list(order_id=order_id, limit=limit, offset=offset)
    return PaymentPage(
        items=[PaymentRead.model_validate(payment) for payment in payments],
        total=total,
        limit=limit,
        offset=offset,
    )


@router.get(
    "/{payment_id}",
    response_model=PaymentRead,
    summary="Get a payment by id",
    responses={404: {"description": "Payment not found"}},
)
def get_payment(payment_id: str, service: PaymentServiceDep) -> PaymentRead:
    return PaymentRead.model_validate(service.get(payment_id))
