from app.core.security import hash_password, verify_password


def test_hash_is_salted_so_equal_passwords_differ():
    first = hash_password("same-password", iterations=1000)
    second = hash_password("same-password", iterations=1000)
    assert first != second


def test_verify_accepts_the_original_password():
    encoded = hash_password("correct horse", iterations=1000)
    assert verify_password("correct horse", encoded) is True


def test_verify_rejects_a_wrong_password():
    encoded = hash_password("correct horse", iterations=1000)
    assert verify_password("wrong horse", encoded) is False


def test_verify_rejects_a_malformed_hash():
    assert verify_password("anything", "not-a-real-hash") is False
