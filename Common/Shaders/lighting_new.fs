#version 330

const int MAX_POINT_LIGHTS = 2;
const int MAX_SPOT_LIGHTS = 2;

in vec4 LightSpacePos; // required only for shadow mapping
in vec2 TexCoord0;
in vec3 Normal0;
in vec3 LocalPos0;
in vec3 WorldPos0;

out vec4 FragColor;

struct BaseLight
{
    vec3 Color;
    float AmbientIntensity;
    float DiffuseIntensity;
};

struct DirectionalLight
{
    BaseLight Base;
    vec3 Direction;
};

struct Attenuation
{
    float Constant;
    float Linear;
    float Exp;
};

struct PointLight
{
    BaseLight Base;
    vec3 LocalPos; // used for lighting calculations
    vec3 WorldPos; // used for point light shadow mapping
    Attenuation Atten;
};

struct SpotLight
{
    PointLight Base;
    vec3 Direction;
    float Cutoff;
};

struct Material
{
    vec3 AmbientColor;
    vec3 DiffuseColor;
    vec3 SpecularColor;
};

uniform DirectionalLight gDirectionalLight;
uniform int gNumPointLights;
uniform PointLight gPointLights[MAX_POINT_LIGHTS];
uniform int gNumSpotLights;
uniform SpotLight gSpotLights[MAX_SPOT_LIGHTS];
uniform Material gMaterial;
uniform sampler2D gSampler;
uniform sampler2D gSamplerSpecularExponent;
uniform sampler2D gShadowMap;        // required only for shadow mapping (spot/directional light)
uniform samplerCube gShadowCubeMap;  // required only for shadow mapping (point light)
uniform vec3 gCameraLocalPos;
uniform vec3 gCameraWorldPos;
uniform vec4 gColorMod = vec4(1);
uniform float gRimLightPower = 2.0;
uniform bool gRimLightEnabled = false;
uniform bool gCellShadingEnabled = false;
uniform bool gEnableSpecularExponent = false;

// Fog
uniform float gExpFogDensity = 1.0;
uniform bool gExpSquaredFogEnabled = false;
uniform float gLayeredFogTop = -1.0;
uniform float gFogStart = -1.0;
uniform float gFogEnd = -1.0;
uniform float gFogTime = -1.0;
uniform vec3 gFogColor = vec3(0.0, 0.0, 0.0);

const int toon_color_levels = 4;
const float toon_scale_factor = 1.0f / toon_color_levels;

float CalcRimLightFactor(vec3 PixelToCamera, vec3 Normal)
{
    float RimFactor = dot(PixelToCamera, Normal);
    RimFactor = 1.0 - RimFactor;
    RimFactor = max(0.0, RimFactor);
    RimFactor = pow(RimFactor, gRimLightPower);
    return RimFactor;
}


float CalcShadowFactorPointLight(vec3 LightToPixel)
{
    float Distance = length(LightToPixel);

    LightToPixel.y = -LightToPixel.y;

    float SampledDistance = texture(gShadowCubeMap, LightToPixel).r;

    float bias = 0.015;

    if (SampledDistance + bias < Distance)
        return 0.25;
    else
        return 1.0;
}


float CalcShadowFactor(vec3 LightDirection, vec3 Normal)
{
    vec3 ProjCoords = LightSpacePos.xyz / LightSpacePos.w;
    vec2 UVCoords;
    UVCoords.x = 0.5 * ProjCoords.x + 0.5;
    UVCoords.y = 0.5 * ProjCoords.y + 0.5;
    float z = 0.5 * ProjCoords.z + 0.5;
    float Depth = texture(gShadowMap, UVCoords).x;

    float DiffuseFactor = dot(Normal, -LightDirection);

    float bias = mix(0.001, 0.0, DiffuseFactor);

    if (Depth + bias < z)
        return 0.05;
    else
        return 1.0;
}


vec4 CalcLightInternal(BaseLight Light, vec3 LightDirection, vec3 Normal,
                       float ShadowFactor)
{
    vec4 AmbientColor = vec4(Light.Color, 1.0f) *
                        Light.AmbientIntensity *
                        vec4(gMaterial.AmbientColor, 1.0f);

    float DiffuseFactor = dot(Normal, -LightDirection);

    vec4 DiffuseColor = vec4(0, 0, 0, 0);
    vec4 SpecularColor = vec4(0, 0, 0, 0);
    vec4 RimColor = vec4(0, 0, 0, 0);

    if (DiffuseFactor > 0) {
        if (gCellShadingEnabled) {
            DiffuseFactor = ceil(DiffuseFactor * toon_color_levels) * toon_scale_factor;
        }

        DiffuseColor = vec4(Light.Color, 1.0f) *
                       Light.DiffuseIntensity *
                       vec4(gMaterial.DiffuseColor, 1.0f) *
                       DiffuseFactor;

        vec3 PixelToCamera = normalize(gCameraLocalPos - LocalPos0);
        vec3 LightReflect = normalize(reflect(LightDirection, Normal));
        float SpecularFactor = dot(PixelToCamera, LightReflect);

        if (!gCellShadingEnabled && (SpecularFactor > 0)) {
            float SpecularExponent = 128.0;

            if (gEnableSpecularExponent) {
                SpecularExponent = texture2D(gSamplerSpecularExponent, TexCoord0).r * 255.0;
            }

            SpecularFactor = pow(SpecularFactor, SpecularExponent);
            SpecularColor = vec4(Light.Color, 1.0f) *
                            Light.DiffuseIntensity * // using the diffuse intensity for diffuse/specular
                            vec4(gMaterial.SpecularColor, 1.0f) *
                            SpecularFactor;
        }

        if (gRimLightEnabled) {
           float RimFactor = CalcRimLightFactor(PixelToCamera, Normal);
           RimColor = DiffuseColor * RimFactor;
        }
    }

    return (AmbientColor + ShadowFactor * (DiffuseColor + SpecularColor + RimColor));
}


vec4 CalcDirectionalLight(vec3 Normal)
{
    float ShadowFactor = CalcShadowFactor(gDirectionalLight.Direction, Normal);
    return CalcLightInternal(gDirectionalLight.Base, gDirectionalLight.Direction, Normal, ShadowFactor);
}


vec4 CalcPointLight(PointLight l, vec3 Normal)
{
    vec3 LightWorldDir = WorldPos0 - l.WorldPos;
    float ShadowFactor = CalcShadowFactorPointLight(LightWorldDir);

    vec3 LightLocalDir = LocalPos0 - l.LocalPos;
    float Distance = length(LightLocalDir);
    LightLocalDir = normalize(LightLocalDir);
    vec4 Color = CalcLightInternal(l.Base, LightLocalDir, Normal, ShadowFactor);
    float Attenuation =  l.Atten.Constant +
                         l.Atten.Linear * Distance +
                         l.Atten.Exp * Distance * Distance;

    return Color / Attenuation;
}


vec4 CalcSpotLight(SpotLight l, vec3 Normal)
{
    vec3 LightToPixel = normalize(LocalPos0 - l.Base.LocalPos);
    float SpotFactor = dot(LightToPixel, l.Direction);

    if (SpotFactor > l.Cutoff) {
        vec4 Color = CalcPointLight(l.Base, Normal);
        float SpotLightIntensity = (1.0 - (1.0 - SpotFactor)/(1.0 - l.Cutoff));
        return Color * SpotLightIntensity;
    }
    else {
        return vec4(0,0,0,0);
    }
}


float CalcLinearFogFactor()
{
    float CameraToPixelDist = length(WorldPos0 - gCameraWorldPos);
    float FogRange = gFogEnd - gFogStart;
    float FogDist = gFogEnd - CameraToPixelDist;
    float FogFactor = FogDist / FogRange;
    FogFactor = clamp(FogFactor, 0.0, 1.0);
    return FogFactor;
}


float CalcExpFogFactor()
{
    float CameraToPixelDist = length(WorldPos0 - gCameraWorldPos);
    float DistRatio = 4.0 * CameraToPixelDist / gFogEnd;
    float FogFactor = 1.0;

    if (gExpSquaredFogEnabled) {
        FogFactor = exp(-DistRatio * gExpFogDensity * DistRatio * gExpFogDensity);
    } else {
        FogFactor = exp(-DistRatio * gExpFogDensity);
    }

    return FogFactor;
}


float CalcLayeredFogFactor()
{
    vec3 CameraProj = gCameraWorldPos;
    CameraProj.y = 0.0;

    vec3 PixelProj = WorldPos0;
    PixelProj.y = 0.0;

    float DeltaD = length(CameraProj - PixelProj) / gFogEnd;

    float DeltaY = 0.0f;
    float DensityIntegral = 0.0f;

    if (gCameraWorldPos.y > gLayeredFogTop) { // The camera is above the top of the fog
        if (WorldPos0.y < gLayeredFogTop) {   // The pixel is inside the fog
            DeltaY = (gLayeredFogTop - WorldPos0.y) / gLayeredFogTop;
            DensityIntegral = DeltaY * DeltaY * 0.5;
        }
    } else {
        if (WorldPos0.y < gLayeredFogTop) {
            DeltaY = abs(gCameraWorldPos.y - WorldPos0.y) / gLayeredFogTop;
            float DeltaCamera = (gLayeredFogTop - gCameraWorldPos.y) / gLayeredFogTop;
            float DensityIntegralCamera = DeltaCamera * DeltaCamera * 0.5;
            float DeltaPixel = (gLayeredFogTop - WorldPos0.y) / gLayeredFogTop;
            float DensityIntegralPixel = DeltaPixel * DeltaPixel * 0.5;
            DensityIntegral = abs(DensityIntegralCamera - DensityIntegralPixel);
        } else {
            DeltaY = (gLayeredFogTop - gCameraWorldPos.y) / gLayeredFogTop;
            DensityIntegral = DeltaY * DeltaY * 0.5;
        }
    }

    float FogDensity = 0.0;

    if (DeltaY != 0) {
        FogDensity = (sqrt(1.0 + ((DeltaD / DeltaY) * (DeltaD / DeltaY)))) * DensityIntegral;
    }

    float FogFactor = exp(-FogDensity);

    return FogFactor;
}


#define PI 3.1415926535897932384626433832795

float CalcAnimatedFogFactor()
{
    float CameraToPixelDist = length(WorldPos0 - gCameraWorldPos);

    float DistRatio = CameraToPixelDist / gFogEnd;

    float ExpFogFactor = exp(-DistRatio * gExpFogDensity);

    float x = WorldPos0.x / 20.0;
    float y = WorldPos0.y / 20.0;
    float z = WorldPos0.z / 20.0;

    float AnimFactor = -(1.0 +
                         0.5 * cos(5.0 * PI * z + gFogTime) +
                         0.2 * cos(7.0 * PI * (z + 0.1 * x)) +
                         0.2 * cos(5.0 * PI * (z - 0.05 * x)) +
                         0.1 * cos(PI * x) * cos(PI * z / 2.0));

    float FogFactor = ExpFogFactor + (CameraToPixelDist / gFogEnd) * AnimFactor;

    return FogFactor;
}


float CalcFogFactor()
{
    float FogFactor = 1.0;

    if (gFogTime > 0.0) {
       FogFactor = CalcAnimatedFogFactor();
    } else if (gLayeredFogTop > 0.0) {
        FogFactor = CalcLayeredFogFactor();
    } else if (gFogStart >= 0.0) {
        FogFactor = CalcLinearFogFactor();
    } else {
        FogFactor = CalcExpFogFactor();
    }

    return FogFactor;
}


void main()
{
    vec3 Normal = normalize(Normal0);
    vec4 TotalLight = CalcDirectionalLight(Normal);

    for (int i = 0 ;i < gNumPointLights ;i++) {
        TotalLight += CalcPointLight(gPointLights[i], Normal);
    }

    for (int i = 0 ;i < gNumSpotLights ;i++) {
        TotalLight += CalcSpotLight(gSpotLights[i], Normal);
    }

    vec4 TempColor = texture2D(gSampler, TexCoord0.xy) * TotalLight;

    if (gFogColor != vec3(0)) {
        float FogFactor = CalcFogFactor();

        TempColor = mix(vec4(gFogColor, 1.0), TempColor, FogFactor);
    }

    FragColor =  TempColor * gColorMod;
}
