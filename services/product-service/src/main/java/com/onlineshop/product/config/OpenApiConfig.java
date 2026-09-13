package com.onlineshop.product.config;

import io.swagger.v3.oas.models.OpenAPI;
import io.swagger.v3.oas.models.info.Info;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

@Configuration
public class OpenApiConfig {

    @Bean
    public OpenAPI productServiceOpenApi() {
        return new OpenAPI().info(new Info()
                .title("Product Service")
                .version("0.1.0")
                .description("Owns the product catalogue and stock levels for the Online Shop "
                        + "platform. Other services read products through this API and never "
                        + "touch its database."));
    }
}
