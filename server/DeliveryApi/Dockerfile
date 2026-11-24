# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY DeliveryApi.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release --no-restore -o /out

# Run
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
# Listen on all interfaces inside the container (outside Docker the server binds to localhost:7070).
ENV ASPNETCORE_URLS=http://+:7070 \
    ASPNETCORE_HTTP_PORTS=
EXPOSE 7070
ENTRYPOINT ["dotnet", "DeliveryApi.dll"]
