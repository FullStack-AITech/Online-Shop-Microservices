package com.onlineshop.product.exception;

/** Raised when a reservation asks for more units than the catalogue holds. */
public class InsufficientStockException extends RuntimeException {

    private final String productId;
    private final int available;
    private final int requested;

    public InsufficientStockException(String productId, int available, int requested) {
        super("Product '" + productId + "' has " + available + " units available but "
                + requested + " were requested");
        this.productId = productId;
        this.available = available;
        this.requested = requested;
    }

    public String getProductId() {
        return productId;
    }

    public int getAvailable() {
        return available;
    }

    public int getRequested() {
        return requested;
    }
}
