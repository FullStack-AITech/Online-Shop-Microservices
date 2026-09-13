package com.onlineshop.product;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import com.onlineshop.product.domain.Product;
import com.onlineshop.product.exception.InsufficientStockException;
import java.math.BigDecimal;
import org.junit.jupiter.api.Test;

/** Unit tests for the aggregate's invariants. No Spring context needed. */
class ProductTest {

    private Product product(int stock) {
        return new Product("SKU-1", "Keyboard", "Mechanical", new BigDecimal("49.99"), "GBP", stock);
    }

    @Test
    void reservingStockReducesTheAvailableQuantity() {
        Product product = product(10);
        product.reserveStock(3);
        assertThat(product.getStockQuantity()).isEqualTo(7);
    }

    @Test
    void reservingMoreThanAvailableIsRejected() {
        Product product = product(2);
        assertThatThrownBy(() -> product.reserveStock(3))
                .isInstanceOf(InsufficientStockException.class);
        assertThat(product.getStockQuantity()).isEqualTo(2);
    }

    @Test
    void reservingTheExactRemainingStockIsAllowed() {
        Product product = product(5);
        product.reserveStock(5);
        assertThat(product.getStockQuantity()).isZero();
    }

    @Test
    void reservingANonPositiveQuantityIsRejected() {
        Product product = product(5);
        assertThatThrownBy(() -> product.reserveStock(0))
                .isInstanceOf(IllegalArgumentException.class);
    }

    @Test
    void releasingStockReturnsUnitsToTheCatalogue() {
        Product product = product(1);
        product.releaseStock(4);
        assertThat(product.getStockQuantity()).isEqualTo(5);
    }

    @Test
    void newProductsAreActive() {
        assertThat(product(1).isActive()).isTrue();
    }

    @Test
    void updateDetailsLeavesNullFieldsUntouched() {
        Product product = product(1);
        product.updateDetails("New name", null, null, null);
        assertThat(product.getName()).isEqualTo("New name");
        assertThat(product.getDescription()).isEqualTo("Mechanical");
        assertThat(product.getPrice()).isEqualByComparingTo("49.99");
        assertThat(product.isActive()).isTrue();
    }
}
