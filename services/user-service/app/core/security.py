"""Password hashing.

Uses PBKDF2-HMAC-SHA256 from the standard library so the service has no native build
dependencies. For production, Argon2id (``pwdlib[argon2]``) is the stronger choice; the
stored hash carries its algorithm and parameters so it can be migrated per user on login.
"""

import hashlib
import hmac
import secrets

_ALGORITHM = "pbkdf2_sha256"
_SALT_BYTES = 16


def hash_password(password: str, *, iterations: int) -> str:
    """Return an encoded hash of ``password`` as ``algorithm$iterations$salt$digest``."""
    salt = secrets.token_hex(_SALT_BYTES)
    digest = _derive(password, salt, iterations)
    return f"{_ALGORITHM}${iterations}${salt}${digest}"


def verify_password(password: str, encoded_hash: str) -> bool:
    """Check ``password`` against a hash produced by :func:`hash_password`."""
    try:
        algorithm, iterations, salt, digest = encoded_hash.split("$")
    except ValueError:
        return False
    if algorithm != _ALGORITHM:
        return False
    try:
        expected = _derive(password, salt, int(iterations))
    except ValueError:
        return False
    return hmac.compare_digest(expected, digest)


def _derive(password: str, salt: str, iterations: int) -> str:
    return hashlib.pbkdf2_hmac(
        "sha256", password.encode("utf-8"), salt.encode("utf-8"), iterations
    ).hex()
