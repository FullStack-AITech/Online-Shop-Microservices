package com.onlineshop.product.exception;

/** Raised when a product id or SKU does not resolve to a catalogue entry. */
public class ProductNotFoundException extends RuntimeException {

    public ProductNotFoundException(String productId) {
        super("Product '" + productId + "' was not found");
    }
}
