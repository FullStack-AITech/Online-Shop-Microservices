package com.onlineshop.notification.dto;

import java.util.List;
import java.util.function.Function;
import org.springframework.data.domain.Page;

/**
 * A stable pagination envelope.
 *
 * <p>Spring's own {@code Page} serialisation is not a contract we want to expose: its JSON
 * shape changes between versions and leaks framework internals to consumers.
 */
public record PageResponse<T>(List<T> items, long total, int page, int size) {

    public static <E, T> PageResponse<T> of(Page<E> page, Function<E, T> mapper) {
        return new PageResponse<>(
                page.getContent().stream().map(mapper).toList(),
                page.getTotalElements(),
                page.getNumber(),
                page.getSize());
    }
}
