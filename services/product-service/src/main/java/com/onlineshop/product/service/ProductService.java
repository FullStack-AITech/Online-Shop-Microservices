package com.onlineshop.product.service;

import com.onlineshop.product.domain.Product;
import com.onlineshop.product.dto.ProductRequests.CreateProduct;
import com.onlineshop.product.dto.ProductRequests.UpdateProduct;
import com.onlineshop.product.exception.ProductNotFoundException;
import com.onlineshop.product.exception.SkuAlreadyExistsException;
import com.onlineshop.product.repository.ProductRepository;
import org.springframework.dao.DataIntegrityViolationException;
import org.springframework.data.domain.Page;
import org.springframework.data.domain.Pageable;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;
import org.springframework.util.StringUtils;

/** Catalogue business rules. Knows nothing about HTTP. */
@Service
@Transactional(readOnly = true)
public class ProductService {

    private final ProductRepository repository;

    public ProductService(ProductRepository repository) {
        this.repository = repository;
    }

    @Transactional
    public Product create(CreateProduct request) {
        String sku = request.sku().trim().toUpperCase();
        if (repository.existsBySku(sku)) {
            throw new SkuAlreadyExistsException(sku);
        }
        Product product = new Product(
                sku,
                request.name().trim(),
                request.description(),
                request.price(),
                request.currency().toUpperCase(),
                request.stockQuantity());
        try {
            return repository.saveAndFlush(product);
        } catch (DataIntegrityViolationException exception) {
            // The unique index is the real guard against two concurrent creates.
            throw new SkuAlreadyExistsException(sku);
        }
    }

    public Product getById(String id) {
        return repository.findById(id).orElseThrow(() -> new ProductNotFoundException(id));
    }

    public Product getBySku(String sku) {
        return repository.findBySku(sku.trim().toUpperCase())
                .orElseThrow(() -> new ProductNotFoundException(sku));
    }

    /** Lists active products, optionally filtered by a free-text term. */
    public Page<Product> list(String searchTerm, Pageable pageable) {
        if (StringUtils.hasText(searchTerm)) {
            return repository.search(searchTerm.trim(), pageable);
        }
        return repository.findByActiveTrue(pageable);
    }

    @Transactional
    public Product update(String id, UpdateProduct request) {
        Product product = getById(id);
        product.updateDetails(
                request.name(), request.description(), request.price(), request.active());
        return repository.save(product);
    }

    /**
     * Soft-delete: orders and invoices reference products long after they leave the
     * catalogue, so rows are deactivated rather than removed.
     */
    @Transactional
    public void deactivate(String id) {
        Product product = getById(id);
        product.updateDetails(null, null, null, false);
        repository.save(product);
    }

    @Transactional
    public Product reserveStock(String id, int quantity) {
        Product product = getById(id);
        product.reserveStock(quantity);
        return repository.save(product);
    }

    @Transactional
    public Product releaseStock(String id, int quantity) {
        Product product = getById(id);
        product.releaseStock(quantity);
        return repository.save(product);
    }
}
