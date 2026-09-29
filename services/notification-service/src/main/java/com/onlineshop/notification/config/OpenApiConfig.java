package com.onlineshop.notification.config;

import io.swagger.v3.oas.models.OpenAPI;
import io.swagger.v3.oas.models.info.Info;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

@Configuration
public class OpenApiConfig {

    @Bean
    public OpenAPI notificationServiceOpenApi() {
        return new OpenAPI().info(new Info()
                .title("Notification Service")
                .version("0.1.0")
                .description("Tells customers about their orders and payments, driven by events "
                        + "from the orders and payments topics. The API is a read-only history "
                        + "of what was sent."));
    }
}
