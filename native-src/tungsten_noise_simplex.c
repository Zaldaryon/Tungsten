/*
 * tungsten_noise_simplex.c
 * Direct port of Vintage Story's NewSimplexNoiseLayer (Evaluate_ImprovedXZ + Noise3_UnrotatedBase)
 * Ported from decompiled VintagestoryAPI 1.22.2.
 *
 * MUST compile with: -ffp-contract=off -fwrapv
 *   -ffp-contract=off: prevents FMA that would differ from C#/.NET
 *   -fwrapv: makes signed integer overflow wrap (same as C# unchecked)
 */
#pragma STDC FP_CONTRACT OFF

#include "tungsten_noise.h"
#include <math.h>
#include <stdint.h>

#define PRIME_X  0x5205402B9270C86FLL
#define PRIME_Y  0x598CD327003817B5LL
#define PRIME_Z  0x5BCC226E9FA0BACBLL
#define HASH_MULTIPLIER 0x53A3F72DEEC546F5LL
#define SEED_FLIP_3D (-0x52D547B2E96ED629LL)

/* Precomputed constants from VS decompiled source (NOT simply -2*PRIME) */
#define XPRIME_SEED2 (-6626342789952991010LL)  /* used in seed2 X contributions */
#define YPRIME_SEED2 (-5541215012557672598LL)  /* used in seed2 Y contributions */
#define ZPRIME_SEED2 (-5217344451269003882LL)  /* used in seed2 Z contributions */

#define ROTATE_3D_ORTHOGONALIZER (-0.211324865405187)
#define ROOT3OVER3 0.577350269189626
#define NORMALIZER_3D 0.2781926117527186

static float GRADIENTS_3D[1024];
static int s_gradients_initialized = 0;

static void init_gradients(void)
{
    if (s_gradients_initialized) return;

    static const float source[192] = {
        2.2247448f, 2.2247448f, -1.0f, 0.0f, 2.2247448f, 2.2247448f, 1.0f, 0.0f,
        3.0862665f, 1.1721513f, 0.0f, 0.0f, 1.1721513f, 3.0862665f, 0.0f, 0.0f,
        -2.2247448f, 2.2247448f, -1.0f, 0.0f, -2.2247448f, 2.2247448f, 1.0f, 0.0f,
        -1.1721513f, 3.0862665f, 0.0f, 0.0f, -3.0862665f, 1.1721513f, 0.0f, 0.0f,
        -1.0f, -2.2247448f, -2.2247448f, 0.0f, 1.0f, -2.2247448f, -2.2247448f, 0.0f,
        0.0f, -3.0862665f, -1.1721513f, 0.0f, 0.0f, -1.1721513f, -3.0862665f, 0.0f,
        -1.0f, -2.2247448f, 2.2247448f, 0.0f, 1.0f, -2.2247448f, 2.2247448f, 0.0f,
        0.0f, -1.1721513f, 3.0862665f, 0.0f, 0.0f, -3.0862665f, 1.1721513f, 0.0f,
        -2.2247448f, -2.2247448f, -1.0f, 0.0f, -2.2247448f, -2.2247448f, 1.0f, 0.0f,
        -3.0862665f, -1.1721513f, 0.0f, 0.0f, -1.1721513f, -3.0862665f, 0.0f, 0.0f,
        -2.2247448f, -1.0f, -2.2247448f, 0.0f, -2.2247448f, 1.0f, -2.2247448f, 0.0f,
        -1.1721513f, 0.0f, -3.0862665f, 0.0f, -3.0862665f, 0.0f, -1.1721513f, 0.0f,
        -2.2247448f, -1.0f, 2.2247448f, 0.0f, -2.2247448f, 1.0f, 2.2247448f, 0.0f,
        -3.0862665f, 0.0f, 1.1721513f, 0.0f, -1.1721513f, 0.0f, 3.0862665f, 0.0f,
        -1.0f, 2.2247448f, -2.2247448f, 0.0f, 1.0f, 2.2247448f, -2.2247448f, 0.0f,
        0.0f, 1.1721513f, -3.0862665f, 0.0f, 0.0f, 3.0862665f, -1.1721513f, 0.0f,
        -1.0f, 2.2247448f, 2.2247448f, 0.0f, 1.0f, 2.2247448f, 2.2247448f, 0.0f,
        0.0f, 3.0862665f, 1.1721513f, 0.0f, 0.0f, 1.1721513f, 3.0862665f, 0.0f,
        2.2247448f, -2.2247448f, -1.0f, 0.0f, 2.2247448f, -2.2247448f, 1.0f, 0.0f,
        1.1721513f, -3.0862665f, 0.0f, 0.0f, 3.0862665f, -1.1721513f, 0.0f, 0.0f,
        2.2247448f, -1.0f, -2.2247448f, 0.0f, 2.2247448f, 1.0f, -2.2247448f, 0.0f,
        3.0862665f, 0.0f, -1.1721513f, 0.0f, 1.1721513f, 0.0f, -3.0862665f, 0.0f,
        2.2247448f, -1.0f, 2.2247448f, 0.0f, 2.2247448f, 1.0f, 2.2247448f, 0.0f,
        1.1721513f, 0.0f, 3.0862665f, 0.0f, 3.0862665f, 0.0f, 1.1721513f, 0.0f
    };

    float scaled[192];
    for (int i = 0; i < 192; i++)
        scaled[i] = (float)((double)source[i] / NORMALIZER_3D);

    int s = 0, d = 0;
    while (d < 1024) {
        if (s == 192) s = 0;
        GRADIENTS_3D[d++] = scaled[s++];
    }

    s_gradients_initialized = 1;
}

static inline int64_t hash_primes(int64_t seed, int64_t xsvp, int64_t ysvp, int64_t zsvp)
{
    return seed ^ xsvp ^ ysvp ^ zsvp;
}

static inline float grad(int64_t hash, float dx, float dy, float dz)
{
    hash *= HASH_MULTIPLIER;
    hash ^= hash >> 58;  /* arithmetic right shift (GCC/Clang guaranteed for signed) */
    int idx = (int)hash & 0x3FC;
    return GRADIENTS_3D[idx] * dx + GRADIENTS_3D[idx | 1] * dy + GRADIENTS_3D[idx | 2] * dz;
}

static float noise3_unrotated_base(int64_t seed, double xr, double yr, double zr)
{
    int xrb = (int)floor(xr);
    int yrb = (int)floor(yr);
    int zrb = (int)floor(zr);
    float xi = (float)(xr - xrb);
    float yi = (float)(yr - yrb);
    float zi = (float)(zr - zrb);

    int64_t xrbp = (int64_t)xrb * PRIME_X;
    int64_t yrbp = (int64_t)yrb * PRIME_Y;
    int64_t zrbp = (int64_t)zrb * PRIME_Z;
    int64_t seed2 = seed ^ SEED_FLIP_3D;

    int xNSign = (int)(-0.5f - xi);
    int yNSign = (int)(-0.5f - yi);
    int zNSign = (int)(-0.5f - zi);

    float ax0 = xi + (float)xNSign;
    float ay0 = yi + (float)yNSign;
    float az0 = zi + (float)zNSign;
    float ax1 = xi - 0.5f;
    float ay1 = yi - 0.5f;
    float az1 = zi - 0.5f;

    /* Contribution 1: near corner */
    float a0 = 0.75f - ax0*ax0 - ay0*ay0 - az0*az0;
    int64_t h0 = hash_primes(seed,
        xrbp + ((int64_t)xNSign & PRIME_X),
        yrbp + ((int64_t)yNSign & PRIME_Y),
        zrbp + ((int64_t)zNSign & PRIME_Z));
    float value = a0*a0*(a0*a0) * grad(h0, ax0, ay0, az0);

    /* Contribution 2: far corner */
    float a1 = 0.75f - ax1*ax1 - ay1*ay1 - az1*az1;
    value += a1*a1*(a1*a1) * grad(
        hash_primes(seed2, xrbp + PRIME_X, yrbp + PRIME_Y, zrbp + PRIME_Z),
        ax1, ay1, az1);

    /* Precompute for conditional contributions */
    float xNSignF2 = (float)((xNSign | 1) << 1) * ax1;
    float yNSignF2 = (float)((yNSign | 1) << 1) * ay1;
    float zNSignF2 = (float)((zNSign | 1) << 1) * az1;
    float xNSignM = (float)(-2 - (xNSign << 2)) * ax1 - 1.0f;
    float yNSignM = (float)(-2 - (yNSign << 2)) * ay1 - 1.0f;
    float zNSignM = (float)(-2 - (zNSign << 2)) * az1 - 1.0f;

    int flag = 0, flag2 = 0, flag3 = 0;

    /* X-axis primary */
    float aX = xNSignF2 + a0;
    if (aX > 0.0f) {
        value += aX*aX*(aX*aX) * grad(
            hash_primes(seed, xrbp + (~(int64_t)xNSign & PRIME_X), yrbp + ((int64_t)yNSign & PRIME_Y), zrbp + ((int64_t)zNSign & PRIME_Z)),
            ax0 - (float)(xNSign | 1), ay0, az0);
    } else {
        float aYZ = yNSignF2 + zNSignF2 + a0;
        if (aYZ > 0.0f) {
            value += aYZ*aYZ*(aYZ*aYZ) * grad(
                hash_primes(seed, xrbp + ((int64_t)xNSign & PRIME_X), yrbp + (~(int64_t)yNSign & PRIME_Y), zrbp + (~(int64_t)zNSign & PRIME_Z)),
                ax0, ay0 - (float)(yNSign | 1), az0 - (float)(zNSign | 1));
        }
        float aXM = xNSignM + a1;
        if (aXM > 0.0f) {
            value += aXM*aXM*(aXM*aXM) * grad(
                hash_primes(seed2, xrbp + ((int64_t)xNSign & XPRIME_SEED2), yrbp + PRIME_Y, zrbp + PRIME_Z),
                (float)(xNSign | 1) + ax1, ay1, az1);
            flag = 1;
        }
    }

    /* Y-axis primary */
    float aY = yNSignF2 + a0;
    if (aY > 0.0f) {
        value += aY*aY*(aY*aY) * grad(
            hash_primes(seed, xrbp + ((int64_t)xNSign & PRIME_X), yrbp + (~(int64_t)yNSign & PRIME_Y), zrbp + ((int64_t)zNSign & PRIME_Z)),
            ax0, ay0 - (float)(yNSign | 1), az0);
    } else {
        float aXZ = xNSignF2 + zNSignF2 + a0;
        if (aXZ > 0.0f) {
            value += aXZ*aXZ*(aXZ*aXZ) * grad(
                hash_primes(seed, xrbp + (~(int64_t)xNSign & PRIME_X), yrbp + ((int64_t)yNSign & PRIME_Y), zrbp + (~(int64_t)zNSign & PRIME_Z)),
                ax0 - (float)(xNSign | 1), ay0, az0 - (float)(zNSign | 1));
        }
        float aYM = yNSignM + a1;
        if (aYM > 0.0f) {
            value += aYM*aYM*(aYM*aYM) * grad(
                hash_primes(seed2, xrbp + PRIME_X, yrbp + ((int64_t)yNSign & YPRIME_SEED2), zrbp + PRIME_Z),
                ax1, (float)(yNSign | 1) + ay1, az1);
            flag2 = 1;
        }
    }

    /* Z-axis primary */
    float aZ = zNSignF2 + a0;
    if (aZ > 0.0f) {
        value += aZ*aZ*(aZ*aZ) * grad(
            hash_primes(seed, xrbp + ((int64_t)xNSign & PRIME_X), yrbp + ((int64_t)yNSign & PRIME_Y), zrbp + (~(int64_t)zNSign & PRIME_Z)),
            ax0, ay0, az0 - (float)(zNSign | 1));
    } else {
        float aXY = xNSignF2 + yNSignF2 + a0;
        if (aXY > 0.0f) {
            value += aXY*aXY*(aXY*aXY) * grad(
                hash_primes(seed, xrbp + (~(int64_t)xNSign & PRIME_X), yrbp + (~(int64_t)yNSign & PRIME_Y), zrbp + ((int64_t)zNSign & PRIME_Z)),
                ax0 - (float)(xNSign | 1), ay0 - (float)(yNSign | 1), az0);
        }
        float aZM = zNSignM + a1;
        if (aZM > 0.0f) {
            value += aZM*aZM*(aZM*aZM) * grad(
                hash_primes(seed2, xrbp + PRIME_X, yrbp + PRIME_Y, zrbp + ((int64_t)zNSign & ZPRIME_SEED2)),
                ax1, ay1, (float)(zNSign | 1) + az1);
            flag3 = 1;
        }
    }

    /* Fallback contributions */
    if (!flag) {
        float aFallbackX = yNSignM + zNSignM + a1;
        if (aFallbackX > 0.0f) {
            value += aFallbackX*aFallbackX*(aFallbackX*aFallbackX) * grad(
                hash_primes(seed2, xrbp + PRIME_X, yrbp + ((int64_t)yNSign & YPRIME_SEED2), zrbp + ((int64_t)zNSign & ZPRIME_SEED2)),
                ax1, (float)(yNSign | 1) + ay1, (float)(zNSign | 1) + az1);
        }
    }
    if (!flag2) {
        float aFallbackY = xNSignM + zNSignM + a1;
        if (aFallbackY > 0.0f) {
            value += aFallbackY*aFallbackY*(aFallbackY*aFallbackY) * grad(
                hash_primes(seed2, xrbp + ((int64_t)xNSign & XPRIME_SEED2), yrbp + PRIME_Y, zrbp + ((int64_t)zNSign & ZPRIME_SEED2)),
                (float)(xNSign | 1) + ax1, ay1, (float)(zNSign | 1) + az1);
        }
    }
    if (!flag3) {
        float aFallbackZ = xNSignM + yNSignM + a1;
        if (aFallbackZ > 0.0f) {
            value += aFallbackZ*aFallbackZ*(aFallbackZ*aFallbackZ) * grad(
                hash_primes(seed2, xrbp + ((int64_t)xNSign & XPRIME_SEED2), yrbp + ((int64_t)yNSign & YPRIME_SEED2), zrbp + PRIME_Z),
                (float)(xNSign | 1) + ax1, (float)(yNSign | 1) + ay1, az1);
        }
    }

    return value;
}

/* Public API: matches VS's NewSimplexNoiseLayer.Evaluate_ImprovedXZ */
float tungsten_evaluate_improved_xz(int64_t seed, double x, double y, double z)
{
    init_gradients();
    double xz = x + z;
    double s2 = xz * ROTATE_3D_ORTHOGONALIZER;
    double yy = y * ROOT3OVER3;
    double xr = x + s2 + yy;
    double zr = z + s2 + yy;
    double yr = xz * (-ROOT3OVER3) + yy;
    return noise3_unrotated_base(seed, xr, yr, zr);
}
