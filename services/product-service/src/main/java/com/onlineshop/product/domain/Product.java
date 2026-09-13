package com.onlineshop.product.domain;

import com.onlineshop.product.exception.InsufficientStockException;
import jakarta.persistence.Column;
import jakarta.persistence.Entity;
import jakarta.persistence.Id;
import jakarta.persistence.Table;
import jakarta.persistence.Version;
import java.math.BigDecimal;
import java.time.Instant;
import java.util.UUID;

/**
 * A product in the catalogue.
 *
 * <p>Money is stored as {@link BigDecimal} with an explicit currency: floating point is
 * never acceptable for prices, and a currency-less amount is meaningless across regions.
 */
@Entity
@Table(name = "products")
public class Product {

    @Id
    @Column(length = 36, nullable = false, updatable = false)
    private String id;

    @Column(nullable = false, unique = true, length = 64)
    private String sku;

    @Column(nullable = false, length = 200)
    private String name;

    @Column(length = 2000)
    private String description;

    @Column(nullable = false, precision = 12, scale = 2)
    private BigDecimal price;

    @Column(nullable = false, length = 3)
    private String currency;

    @Column(name = "stock_quantity", nullable = false)
    private int stockQuantity;

    @Column(nullable = false)
    private boolean active;

    @Column(name = "created_at", nullable = false, updatable = false)
    private Instant createdAt;

    @Column(name = "updated_at", nullable = false)
    private Instant updatedAt;

    /**
     * Optimistic locking. Two concurrent stock adjustments must not silently overwrite one
     * another, and the Order Service will contend for stock on the same rows.
     *
     * <p>Boxed rather than primitive on purpose: ids are assigned in the constructor, so
     * Spring Data cannot tell a new entity from a detached one by id alone. A null version
     * marks the entity as new, which makes {@code save} issue an insert instead of a merge
     * preceded by a pointless select.
     */
    @Version
    @Column(nullable = false)
    private Long version;

    protected Product() {
        // Required by JPA.
    }

    public Product(String sku, String name, String description, BigDecimal price,
                   String currency, int stockQuantity) {
        this.id = UUID.randomUUID().toString();
        this.sku = sku;
        this.name = name;
        this.description = description;
        this.price = price;
        this.currency = currency;
        this.stockQuantity = stockQuantity;
        this.active = true;
        Instant now = Instant.now();
        this.createdAt = now;
        this.updatedAt = now;
    }

    /**
     * Reduces stock when an order reserves units.
     *
     * @throws InsufficientStockException when fewer units are available than requested
     */
    public void reserveStock(int quantity) {
        if (quantity <= 0) {
            throw new IllegalArgumentException("Quantity to reserve must be positive");
        }
        if (quantity > stockQuantity) {
            throw new InsufficientStockException(id, stockQuantity, quantity);
        }
        stockQuantity -= quantity;
        touch();
    }

    /** Returns stock to the catalogue when an order is cancelled or a payment fails. */
    public void releaseStock(int quantity) {
        if (quantity <= 0) {
            throw new IllegalArgumentException("Quantity to release must be positive");
        }
        stockQuantity += quantity;
        touch();
    }

    public void updateDetails(String name, String description, BigDecimal price, Boolean active) {
        if (name != null) {
            this.name = name;
        }
        if (description != null) {
            this.description = description;
        }
        if (price != null) {
            this.price = price;
        }
        if (active != null) {
            this.active = active;
        }
        touch();
    }

    private void touch() {
        this.updatedAt = Instant.now();
    }

    public String getId() {
        return id;
    }

    public String getSku() {
        return sku;
    }

    public String getName() {
        return name;
    }

    public String getDescription() {
        return description;
    }

    public BigDecimal getPrice() {
        return price;
    }

    public String getCurrency() {
        return currency;
    }

    public int getStockQuantity() {
        return stockQuantity;
    }

    public boolean isActive() {
        return active;
    }

    public Instant getCreatedAt() {
        return createdAt;
    }

    public Instant getUpdatedAt() {
        return updatedAt;
    }

    public Long getVersion() {
        return version;
    }
}
