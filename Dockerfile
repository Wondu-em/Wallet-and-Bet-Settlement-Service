FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for efficient layer caching
COPY ["WalletBet.slnx", "./"]
COPY ["src/Wallet.Domain/Wallet.Domain.csproj", "src/Wallet.Domain/"]
COPY ["src/Wallet.Application/Wallet.Application.csproj", "src/Wallet.Application/"]
COPY ["src/Wallet.Infrastructure/Wallet.Infrastructure.csproj", "src/Wallet.Infrastructure/"]
COPY ["src/Wallet.Api/Wallet.Api.csproj", "src/Wallet.Api/"]

RUN dotnet restore "src/Wallet.Api/Wallet.Api.csproj"

# Copy remaining source code
COPY . .
WORKDIR /src/src/Wallet.Api
RUN dotnet publish "Wallet.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Wallet.Api.dll"]
