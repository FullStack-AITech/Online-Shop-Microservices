package com.onlineshop.product.web;

import com.onlineshop.product.domain.Product;
import com.onlineshop.product.dto.PageResponse;
import com.onlineshop.product.dto.ProductRequests.CreateProduct;
import com.onlineshop.product.dto.ProductRequests.StockAdjustment;
import com.onlineshop.product.dto.ProductRequests.UpdateProduct;
import com.onlineshop.product.dto.ProductResponse;
import com.onlineshop.product.service.ProductService;
import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;
import jakarta.validation.Valid;
import java.net.URI;
import org.springframework.data.domain.Page;
import org.springframework.data.domain.PageRequest;
import org.springframework.data.domain.Sort;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.DeleteMapping;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PatchMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

/** Products REST API (v1). */
@RestController
@RequestMapping("/api/v1/products")
@Tag(name = "products", description = "Catalogue and stock operations")
public class ProductController {

    private static final int MAX_PAGE_SIZE = 100;

    private final ProductService service;

    public ProductController(ProductService service) {
        this.service = service;
    }

    @PostMapping
    @Operation(summary = "Create a product")
    public ResponseEntity<ProductResponse> create(@Valid @RequestBody CreateProduct request) {
        ProductResponse created = ProductResponse.from(service.create(request));
        // 201 responses carry the location of the new resource.
        return ResponseEntity.created(URI.create("/api/v1/products/" + created.id())).body(created);
    }

    @GetMapping
    @Operation(summary = "List or search active products")
    public PageResponse<ProductResponse> list(
            @RequestParam(required = false) String search,
            @RequestParam(defaultValue = "0") int page,
            @RequestParam(defaultValue = "20") int size) {
        PageRequest pageRequest = PageRequest.of(
                Math.max(page, 0),
                Math.min(Math.max(size, 1), MAX_PAGE_SIZE),
                Sort.by(Sort.Direction.DESC, "createdAt"));
        Page<Product> results = service.list(search, pageRequest);
        return PageResponse.of(results, ProductResponse::from);
    }

    @GetMapping("/{id}")
    @Operation(summary = "Get a product by id")
    public ProductResponse getById(@PathVariable String id) {
        return ProductResponse.from(service.getById(id));
    }

    @GetMapping("/sku/{sku}")
    @Operation(summary = "Get a product by SKU")
    public ProductResponse getBySku(@PathVariable String sku) {
        return ProductResponse.from(service.getBySku(sku));
    }

    @PatchMapping("/{id}")
    @Operation(summary = "Update a product")
    public ProductResponse update(
            @PathVariable String id, @Valid @RequestBody UpdateProduct request) {
        return ProductResponse.from(service.update(id, request));
    }

    @DeleteMapping("/{id}")
    @Operation(summary = "Deactivate a product (soft delete)")
    public ResponseEntity<Void> deactivate(@PathVariable String id) {
        service.deactivate(id);
        return ResponseEntity.noContent().build();
    }

    @PostMapping("/{id}/stock/reserve")
    @Operation(summary = "Reserve stock for an order")
    public ProductResponse reserve(
            @PathVariable String id, @Valid @RequestBody StockAdjustment request) {
        return ProductResponse.from(service.reserveStock(id, request.quantity()));
    }

    @PostMapping("/{id}/stock/release")
    @Operation(summary = "Return reserved stock to the catalogue")
    public ProductResponse release(
            @PathVariable String id, @Valid @RequestBody StockAdjustment request) {
        return ProductResponse.from(service.releaseStock(id, request.quantity()));
    }
}
