package com.onlineshop.product;

import static org.hamcrest.Matchers.is;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.delete;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.patch;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.header;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

import com.fasterxml.jackson.databind.ObjectMapper;
import com.onlineshop.product.repository.ProductRepository;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.web.servlet.AutoConfigureMockMvc;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.http.MediaType;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.test.web.servlet.MvcResult;

/** Exercises the Products API end to end against an in-memory database. */
@SpringBootTest
@AutoConfigureMockMvc
@ActiveProfiles("test")
class ProductApiIntegrationTest {

    @Autowired
    private MockMvc mockMvc;

    @Autowired
    private ObjectMapper objectMapper;

    @Autowired
    private ProductRepository repository;

    private static final String VALID_PRODUCT = """
            {
              "sku": "KB-001",
              "name": "Mechanical Keyboard",
              "description": "Tactile switches",
              "price": 49.99,
              "currency": "GBP",
              "stockQuantity": 10
            }
            """;

    @BeforeEach
    void clearCatalogue() {
        repository.deleteAll();
    }

    private String createProduct(String json) throws Exception {
        MvcResult result = mockMvc.perform(post("/api/v1/products")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content(json))
                .andExpect(status().isCreated())
                .andReturn();
        return objectMapper.readTree(result.getResponse().getContentAsString()).get("id").asText();
    }

    @Test
    void createReturns201WithTheStoredProduct() throws Exception {
        mockMvc.perform(post("/api/v1/products")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content(VALID_PRODUCT))
                .andExpect(status().isCreated())
                .andExpect(header().exists("Location"))
                .andExpect(jsonPath("$.sku", is("KB-001")))
                .andExpect(jsonPath("$.stockQuantity", is(10)))
                .andExpect(jsonPath("$.active", is(true)));
    }

    @Test
    void skusAreNormalisedToUppercaseAndMustBeUnique() throws Exception {
        createProduct(VALID_PRODUCT);
        mockMvc.perform(post("/api/v1/products")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content(VALID_PRODUCT.replace("KB-001", "kb-001")))
                .andExpect(status().isConflict())
                .andExpect(jsonPath("$.error", is("sku_already_exists")));
    }

    @Test
    void invalidCurrencyIsRejected() throws Exception {
        mockMvc.perform(post("/api/v1/products")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content(VALID_PRODUCT.replace("\"GBP\"", "\"pounds\"")))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.error", is("validation_failed")));
    }

    @Test
    void negativePriceIsRejected() throws Exception {
        mockMvc.perform(post("/api/v1/products")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content(VALID_PRODUCT.replace("49.99", "-1.00")))
                .andExpect(status().isBadRequest());
    }

    @Test
    void getByIdReturnsTheProduct() throws Exception {
        String id = createProduct(VALID_PRODUCT);
        mockMvc.perform(get("/api/v1/products/{id}", id))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.id", is(id)));
    }

    @Test
    void getByUnknownIdReturns404() throws Exception {
        mockMvc.perform(get("/api/v1/products/{id}", "missing"))
                .andExpect(status().isNotFound())
                .andExpect(jsonPath("$.error", is("product_not_found")));
    }

    @Test
    void getBySkuIsCaseInsensitive() throws Exception {
        createProduct(VALID_PRODUCT);
        mockMvc.perform(get("/api/v1/products/sku/{sku}", "kb-001"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.sku", is("KB-001")));
    }

    @Test
    void listIsPaginated() throws Exception {
        createProduct(VALID_PRODUCT);
        createProduct(VALID_PRODUCT.replace("KB-001", "KB-002"));
        createProduct(VALID_PRODUCT.replace("KB-001", "KB-003"));

        mockMvc.perform(get("/api/v1/products").param("size", "2").param("page", "0"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.total", is(3)))
                .andExpect(jsonPath("$.items.length()", is(2)));
    }

    @Test
    void searchMatchesNameCaseInsensitively() throws Exception {
        createProduct(VALID_PRODUCT);
        createProduct(VALID_PRODUCT.replace("KB-001", "MS-001").replace("Mechanical Keyboard", "Mouse"));

        mockMvc.perform(get("/api/v1/products").param("search", "keyboard"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.total", is(1)))
                .andExpect(jsonPath("$.items[0].sku", is("KB-001")));
    }

    @Test
    void patchUpdatesOnlySuppliedFields() throws Exception {
        String id = createProduct(VALID_PRODUCT);
        mockMvc.perform(patch("/api/v1/products/{id}", id)
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"name\":\"Renamed\"}"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.name", is("Renamed")))
                .andExpect(jsonPath("$.description", is("Tactile switches")));
    }

    @Test
    void deleteDeactivatesRatherThanRemoving() throws Exception {
        String id = createProduct(VALID_PRODUCT);
        mockMvc.perform(delete("/api/v1/products/{id}", id)).andExpect(status().isNoContent());

        // Still retrievable by id, but no longer listed in the catalogue.
        mockMvc.perform(get("/api/v1/products/{id}", id))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.active", is(false)));
        mockMvc.perform(get("/api/v1/products"))
                .andExpect(jsonPath("$.total", is(0)));
    }

    @Test
    void reservingStockReducesTheQuantity() throws Exception {
        String id = createProduct(VALID_PRODUCT);
        mockMvc.perform(post("/api/v1/products/{id}/stock/reserve", id)
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"quantity\":4}"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.stockQuantity", is(6)));
    }

    @Test
    void reservingMoreThanAvailableReturns409() throws Exception {
        String id = createProduct(VALID_PRODUCT);
        mockMvc.perform(post("/api/v1/products/{id}/stock/reserve", id)
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"quantity\":99}"))
                .andExpect(status().isConflict())
                .andExpect(jsonPath("$.error", is("insufficient_stock")));
    }

    @Test
    void releasingStockReturnsUnits() throws Exception {
        String id = createProduct(VALID_PRODUCT);
        mockMvc.perform(post("/api/v1/products/{id}/stock/release", id)
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"quantity\":5}"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.stockQuantity", is(15)));
    }
}
