package com.onlineshop.product.dto;

import com.onlineshop.product.domain.Product;
import java.math.BigDecimal;
import java.time.Instant;

/** Public view of a product. Keeps the wire contract decoupled from the JPA entity. */
public record ProductResponse(
        String id,
        String sku,
        String name,
        String description,
        BigDecimal price,
        String currency,
        int stockQuantity,
        boolean active,
        Instant createdAt,
        Instant updatedAt) {

    public static ProductResponse from(Product product) {
        return new ProductResponse(
                product.getId(),
                product.getSku(),
                product.getName(),
                product.getDescription(),
                product.getPrice(),
                product.getCurrency(),
                product.getStockQuantity(),
                product.isActive(),
                product.getCreatedAt(),
                product.getUpdatedAt());
    }
}
