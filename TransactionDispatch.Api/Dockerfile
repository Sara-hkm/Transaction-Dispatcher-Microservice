# ──────────────────────────────────────────────────────────────────────────────
# Stage 1 – restore & build
# ──────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution + project files first for better layer caching
COPY TransactionDispatch.sln ./
COPY TransactionDispatch.Domain/TransactionDispatch.Domain.csproj                         TransactionDispatch.Domain/
COPY TransactionDispatch.Application/TransactionDispatch.Application.csproj               TransactionDispatch.Application/
COPY TransactionDispatch.Infrastructure/TransactionDispatch.Infrastructure.csproj         TransactionDispatch.Infrastructure/
COPY TransactionDispatch.Api/TransactionDispatch.Api.csproj                               TransactionDispatch.Api/

RUN dotnet restore TransactionDispatch.Api/TransactionDispatch.Api.csproj

# Copy the rest of the source and publish
COPY TransactionDispatch.Domain/           TransactionDispatch.Domain/
COPY TransactionDispatch.Application/      TransactionDispatch.Application/
COPY TransactionDispatch.Infrastructure/   TransactionDispatch.Infrastructure/
COPY TransactionDispatch.Api/              TransactionDispatch.Api/

RUN dotnet publish TransactionDispatch.Api/TransactionDispatch.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ──────────────────────────────────────────────────────────────────────────────
# Stage 2 – runtime image (no SDK, minimal footprint)
# ──────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Install curl for the docker-compose health check, then clean apt cache
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Create a non-root user for security
RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "TransactionDispatch.Api.dll"]
