package com.onlineshop.product.repository;

import com.onlineshop.product.domain.Product;
import java.util.Optional;
import org.springframework.data.domain.Page;
import org.springframework.data.domain.Pageable;
import org.springframework.data.jpa.repository.JpaRepository;
import org.springframework.data.jpa.repository.Query;
import org.springframework.data.repository.query.Param;
import org.springframework.stereotype.Repository;

@Repository
public interface ProductRepository extends JpaRepository<Product, String> {

    Optional<Product> findBySku(String sku);

    boolean existsBySku(String sku);

    Page<Product> findByActiveTrue(Pageable pageable);

    /**
     * Case-insensitive catalogue search over name and SKU.
     *
     * <p>Adequate for a catalogue of this size; a production catalogue would move this to a
     * dedicated search index rather than growing LIKE queries.
     */
    @Query("""
            SELECT p FROM Product p
            WHERE p.active = true
              AND (LOWER(p.name) LIKE LOWER(CONCAT('%', :term, '%'))
                   OR LOWER(p.sku) LIKE LOWER(CONCAT('%', :term, '%')))
            """)
    Page<Product> search(@Param("term") String term, Pageable pageable);
}
