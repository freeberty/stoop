# Build context: ./backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Stoop.Application/Stoop.Application.csproj src/Stoop.Application/
COPY src/Stoop.Infrastructure/Stoop.Infrastructure.csproj src/Stoop.Infrastructure/
COPY src/Stoop.Api/Stoop.Api.csproj src/Stoop.Api/
RUN dotnet restore src/Stoop.Api/Stoop.Api.csproj
COPY src/ src/
RUN dotnet publish src/Stoop.Api/Stoop.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Stoop.Api.dll"]
