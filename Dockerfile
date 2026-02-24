FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

# 1. Install basic tools needed to add the Google repository
RUN apt-get update && apt-get install -y \
    wget \
    curl \
    gnupg \
    ca-certificates \
    --no-install-recommends \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

# 2. Add Google's official repository and install Chrome
# apt-get will automatically resolve and install all the necessary graphical dependencies
RUN curl -fsSL https://dl.google.com/linux/linux_signing_key.pub | gpg --dearmor -o /etc/apt/trusted.gpg.d/google.gpg && \
    echo "deb [arch=amd64] http://dl.google.com/linux/chrome/deb/ stable main" > /etc/apt/sources.list.d/google-chrome.list && \
    apt-get update && \
    apt-get install -y google-chrome-stable --no-install-recommends && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

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
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT:-10000}

ENTRYPOINT ["dotnet", "MatchPredictor.Web.dll"]
