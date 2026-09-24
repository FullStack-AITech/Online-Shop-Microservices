"""End-to-end tests for the Payments API, exercised through HTTP."""

from decimal import Decimal

from app.core.config import Settings
from app.providers import FakePaymentProvider, build_provider

BODY = {"order_id": "o-1", "user_id": "u-1", "amount": "20.00", "currency": "GBP"}


def _pay(client, key="key-1", body=None):
    headers = {"Idempotency-Key": key} if key is not None else {}
    return client.post("/api/v1/payments", json=body or BODY, headers=headers)


def test_first_request_returns_201_and_a_captured_payment(client, provider):
    response = _pay(client)
    assert response.status_code == 201
    body = response.json()
    assert body["status"] == "Captured"
    assert body["order_id"] == "o-1"
    assert body["idempotency_key"] == "key-1"
    # Money crosses the wire as a string, never a float.
    assert body["amount"] == "20.00"
    assert body["provider_reference"].startswith("fake_")
    assert body["failure_reason"] is None
    assert provider.calls == [{"amount": Decimal("20.00"), "currency": "GBP", "reference": "key-1"}]


def test_same_key_twice_is_one_payment_and_one_charge(client, provider):
    first = _pay(client)
    second = _pay(client)
    assert first.status_code == 201
    assert second.status_code == 200
    assert second.json() == first.json()
    assert len(provider.calls) == 1
    assert client.get("/api/v1/payments", params={"order_id": "o-1"}).json()["total"] == 1


def test_an_equal_amount_written_differently_is_the_same_request(client):
    assert _pay(client).status_code == 201
    assert _pay(client, body={**BODY, "amount": "20"}).status_code == 200


def test_reusing_a_key_for_a_different_payment_returns_409(client, provider):
    assert _pay(client).status_code == 201
    response = _pay(client, body={**BODY, "amount": "25.00"})
    assert response.status_code == 409
    assert response.json()["error"] == "idempotency_key_reused"
    assert len(provider.calls) == 1


def test_amount_over_the_threshold_is_declined(client):
    response = _pay(client, key="big", body={**BODY, "amount": "1000.01"})
    assert response.status_code == 201
    body = response.json()
    assert body["status"] == "Failed"
    assert body["failure_reason"] == "card_declined"
    assert body["provider_reference"] is None


def test_amount_at_the_threshold_is_approved(client):
    assert _pay(client, key="edge", body={**BODY, "amount": "1000.00"}).json()["status"] == (
        "Captured"
    )


def test_missing_idempotency_key_returns_422(client, provider):
    assert _pay(client, key=None).status_code == 422
    assert provider.calls == []


def test_invalid_bodies_return_422(client):
    for bad in (
        {**BODY, "amount": "0"},
        {**BODY, "amount": "-5.00"},
        {**BODY, "amount": "1.234"},
        {**BODY, "amount": "10000000000.00"},
        {**BODY, "currency": "gbp"},
        {k: v for k, v in BODY.items() if k != "order_id"},
    ):
        assert _pay(client, key="bad", body=bad).status_code == 422, bad


def test_get_payment_by_id(client):
    created = _pay(client).json()
    response = client.get(f"/api/v1/payments/{created['id']}")
    assert response.status_code == 200
    assert response.json() == created


def test_get_unknown_payment_returns_404(client):
    response = client.get("/api/v1/payments/does-not-exist")
    assert response.status_code == 404
    assert response.json()["error"] == "payment_not_found"


def test_list_filters_by_order_id(client):
    _pay(client, key="a", body={**BODY, "order_id": "o-1"})
    _pay(client, key="b", body={**BODY, "order_id": "o-2"})
    page = client.get("/api/v1/payments", params={"order_id": "o-2"}).json()
    assert page["total"] == 1
    assert [item["order_id"] for item in page["items"]] == ["o-2"]
    assert client.get("/api/v1/payments").json()["total"] == 2


def test_limit_above_the_maximum_is_rejected(client):
    assert client.get("/api/v1/payments", params={"limit": 1000}).status_code == 422


def test_provider_comes_from_settings_and_uses_the_threshold():
    provider = build_provider(Settings(fake_decline_above=Decimal("5.00")))
    assert isinstance(provider, FakePaymentProvider)
    assert provider.charge(amount=Decimal("5.01"), currency="GBP", reference="r").approved is False
    assert provider.charge(amount=Decimal("5.00"), currency="GBP", reference="r").approved is True


def test_fake_provider_is_deterministic_per_reference():
    fake = FakePaymentProvider(decline_above=Decimal("1000.00"))
    first = fake.charge(amount=Decimal("1.00"), currency="GBP", reference="r-1")
    again = fake.charge(amount=Decimal("1.00"), currency="GBP", reference="r-1")
    other = fake.charge(amount=Decimal("1.00"), currency="GBP", reference="r-2")
    assert first == again
    assert first.provider_reference != other.provider_reference


def test_openapi_document_is_served(client):
    response = client.get("/openapi.json")
    assert response.status_code == 200
    assert "/api/v1/payments" in response.json()["paths"]
