FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

# Configure apt for fast cloud builder execution:
# 1. Force IPv4 to eliminate 120s IPv6 DNS/mirror connection timeouts on cloud builders
# 2. Add retries for transient mirror glitches
# 3. Consolidate LightGBM runtime deps and Google Chrome into a single cached layer
RUN echo 'Acquire::ForceIPv4 "true";' > /etc/apt/apt.conf.d/99force-ipv4 \
 && echo 'Acquire::Retries "3";' > /etc/apt/apt.conf.d/80retries \
 && apt-get update && apt-get install -y --no-install-recommends \
    wget \
    curl \
    gnupg \
    ca-certificates \
    libgomp1 \
    libunwind8 \
 && curl -fsSL https://dl.google.com/linux/linux_signing_key.pub | gpg --dearmor -o /etc/apt/trusted.gpg.d/google.gpg \
 && echo "deb [arch=amd64] http://dl.google.com/linux/chrome/deb/ stable main" > /etc/apt/sources.list.d/google-chrome.list \
 && apt-get update && apt-get install -y --no-install-recommends \
    google-chrome-stable \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/* /etc/apt/apt.conf.d/99force-ipv4 /etc/apt/apt.conf.d/80retries

# Set working directory and ports
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["MatchPredictor.Web/MatchPredictor.Web.csproj", "MatchPredictor.Web/"]
COPY ["MatchPredictor.Domain/MatchPredictor.Domain.csproj", "MatchPredictor.Domain/"]
COPY ["MatchPredictor.Infrastructure/MatchPredictor.Infrastructure.csproj", "MatchPredictor.Infrastructure/"]
COPY ["MatchPredictor.Application/MatchPredictor.Application.csproj", "MatchPredictor.Application/"]

RUN dotnet restore "MatchPredictor.Web/MatchPredictor.Web.csproj"

COPY . .
WORKDIR "/src/MatchPredictor.Web"
RUN dotnet build "./MatchPredictor.Web.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./MatchPredictor.Web.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "MatchPredictor.Web.dll"]
