# Build context: ./backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Intersect.Application/Intersect.Application.csproj src/Intersect.Application/
COPY src/Intersect.Infrastructure/Intersect.Infrastructure.csproj src/Intersect.Infrastructure/
COPY src/Intersect.Api/Intersect.Api.csproj src/Intersect.Api/
RUN dotnet restore src/Intersect.Api/Intersect.Api.csproj
COPY src/ src/
RUN dotnet publish src/Intersect.Api/Intersect.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Intersect.Api.dll"]
