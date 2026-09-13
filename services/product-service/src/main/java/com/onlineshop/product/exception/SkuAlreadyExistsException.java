package com.onlineshop.product.exception;

/** Raised when a SKU is reused. SKUs are the catalogue's natural key and must be unique. */
public class SkuAlreadyExistsException extends RuntimeException {

    public SkuAlreadyExistsException(String sku) {
        super("SKU '" + sku + "' is already in use");
    }
}
