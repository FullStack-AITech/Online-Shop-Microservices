package com.onlineshop.product.dto;

import jakarta.validation.constraints.DecimalMin;
import jakarta.validation.constraints.Min;
import jakarta.validation.constraints.NotBlank;
import jakarta.validation.constraints.NotNull;
import jakarta.validation.constraints.Pattern;
import jakarta.validation.constraints.Size;
import java.math.BigDecimal;

/** Inbound contracts. Validation happens at the edge so invalid data never reaches the domain. */
public final class ProductRequests {

    private ProductRequests() {
    }

    public record CreateProduct(
            @NotBlank @Size(max = 64) String sku,
            @NotBlank @Size(max = 200) String name,
            @Size(max = 2000) String description,
            @NotNull @DecimalMin(value = "0.00", inclusive = true) BigDecimal price,
            @NotBlank @Pattern(regexp = "^[A-Z]{3}$", message = "currency must be an ISO-4217 code")
            String currency,
            @Min(0) int stockQuantity) {
    }

    /** Partial update: null fields are left untouched. */
    public record UpdateProduct(
            @Size(max = 200) String name,
            @Size(max = 2000) String description,
            @DecimalMin(value = "0.00", inclusive = true) BigDecimal price,
            Boolean active) {
    }

    public record StockAdjustment(@Min(1) int quantity) {
    }
}
