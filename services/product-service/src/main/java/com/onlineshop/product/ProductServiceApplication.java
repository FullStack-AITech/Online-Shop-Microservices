package com.onlineshop.product;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;

/**
 * Entry point for the Product Service.
 *
 * <p>This service owns the product catalogue and its stock levels. No other service reads
 * or writes its database; they go through the REST API or react to its events.
 */
@SpringBootApplication
public class ProductServiceApplication {

    public static void main(String[] args) {
        SpringApplication.run(ProductServiceApplication.class, args);
    }
}
