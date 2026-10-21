# TFB's way of running an entry, from the repository's root: built with the SDK image, run on the runtime image, on
# port 8080. The runtime settings are aspnetcore's own in TFB (its aspnetcore.dockerfile), so the two compare as equals.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# the image's SDK, whatever its patch: global.json pins the one CI uses
RUN rm -f global.json && dotnet publish benchmarks/CodeFusion.TechEmpower/CodeFusion.TechEmpower.csproj -c Release -o /app --nologo -v q

FROM mcr.microsoft.com/dotnet/runtime:10.0
ENV DOTNET_GCDynamicAdaptationMode=0 \
    DOTNET_ReadyToRun=0 \
    DOTNET_HillClimbing_Disable=1 \
    HOST=0.0.0.0 \
    PORT=8080
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "CodeFusion.TechEmpower.dll"]
