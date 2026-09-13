"""End-to-end tests for the Users API, exercised through HTTP."""


def test_register_returns_201_and_public_fields(client):
    response = client.post(
        "/api/v1/users",
        json={
            "email": "grace@example.com",
            "full_name": "Grace Hopper",
            "password": "nanoseconds1",
        },
    )
    assert response.status_code == 201
    body = response.json()
    assert body["email"] == "grace@example.com"
    assert body["full_name"] == "Grace Hopper"
    assert body["is_active"] is True
    assert body["id"]
    # The hash must never cross the wire.
    assert "password" not in body
    assert "password_hash" not in body


def test_email_is_stored_case_insensitively(client):
    first = client.post(
        "/api/v1/users",
        json={"email": "Alan@Example.com", "full_name": "Alan Turing", "password": "enigma-1936"},
    )
    assert first.status_code == 201
    assert first.json()["email"] == "alan@example.com"

    duplicate = client.post(
        "/api/v1/users",
        json={"email": "ALAN@example.com", "full_name": "Impostor", "password": "enigma-1936"},
    )
    assert duplicate.status_code == 409
    assert duplicate.json()["error"] == "email_already_registered"


def test_duplicate_email_returns_409(client, registered_user):
    response = client.post(
        "/api/v1/users",
        json={"email": registered_user["email"], "full_name": "Someone", "password": "another-pw1"},
    )
    assert response.status_code == 409


def test_invalid_email_returns_422(client):
    response = client.post(
        "/api/v1/users",
        json={"email": "not-an-email", "full_name": "Nobody", "password": "long-enough"},
    )
    assert response.status_code == 422


def test_short_password_returns_422(client):
    response = client.post(
        "/api/v1/users",
        json={"email": "short@example.com", "full_name": "Nobody", "password": "tiny"},
    )
    assert response.status_code == 422


def test_get_user_by_id(client, registered_user):
    response = client.get(f"/api/v1/users/{registered_user['id']}")
    assert response.status_code == 200
    assert response.json()["id"] == registered_user["id"]


def test_get_unknown_user_returns_404(client):
    response = client.get("/api/v1/users/does-not-exist")
    assert response.status_code == 404
    assert response.json()["error"] == "user_not_found"


def test_list_users_is_paginated(client):
    for index in range(3):
        client.post(
            "/api/v1/users",
            json={
                "email": f"user{index}@example.com",
                "full_name": f"User {index}",
                "password": "password123",
            },
        )

    page = client.get("/api/v1/users", params={"limit": 2, "offset": 0}).json()
    assert page["total"] == 3
    assert len(page["items"]) == 2
    assert page["limit"] == 2

    second = client.get("/api/v1/users", params={"limit": 2, "offset": 2}).json()
    assert len(second["items"]) == 1


def test_limit_above_the_maximum_is_rejected(client):
    assert client.get("/api/v1/users", params={"limit": 1000}).status_code == 422


def test_patch_updates_only_supplied_fields(client, registered_user):
    response = client.patch(
        f"/api/v1/users/{registered_user['id']}", json={"full_name": "Ada King"}
    )
    assert response.status_code == 200
    body = response.json()
    assert body["full_name"] == "Ada King"
    assert body["email"] == registered_user["email"]
    assert body["is_active"] is True


def test_patch_unknown_user_returns_404(client):
    assert client.patch("/api/v1/users/missing", json={"full_name": "X"}).status_code == 404


def test_delete_removes_the_user(client, registered_user):
    assert client.delete(f"/api/v1/users/{registered_user['id']}").status_code == 204
    assert client.get(f"/api/v1/users/{registered_user['id']}").status_code == 404


def test_delete_unknown_user_returns_404(client):
    assert client.delete("/api/v1/users/missing").status_code == 404


def test_verify_credentials_accepts_the_right_password(client, registered_user):
    response = client.post(
        "/api/v1/users/credentials:verify",
        json={"email": "ada@example.com", "password": "s3cret-pass"},
    )
    assert response.status_code == 200
    body = response.json()
    assert body["valid"] is True
    assert body["user"]["id"] == registered_user["id"]


def test_verify_credentials_rejects_a_wrong_password(client, registered_user):
    response = client.post(
        "/api/v1/users/credentials:verify",
        json={"email": "ada@example.com", "password": "wrong-password"},
    )
    assert response.status_code == 200
    assert response.json() == {"valid": False, "user": None}


def test_verify_credentials_does_not_reveal_unknown_emails(client):
    response = client.post(
        "/api/v1/users/credentials:verify",
        json={"email": "ghost@example.com", "password": "whatever-1"},
    )
    assert response.status_code == 200
    assert response.json() == {"valid": False, "user": None}


def test_deactivated_users_cannot_authenticate(client, registered_user):
    client.patch(f"/api/v1/users/{registered_user['id']}", json={"is_active": False})
    response = client.post(
        "/api/v1/users/credentials:verify",
        json={"email": "ada@example.com", "password": "s3cret-pass"},
    )
    assert response.json()["valid"] is False


def test_openapi_document_is_served(client):
    response = client.get("/openapi.json")
    assert response.status_code == 200
    assert "/api/v1/users" in response.json()["paths"]
