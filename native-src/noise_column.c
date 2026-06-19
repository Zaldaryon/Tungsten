/*
 * noise_column.c
 * ForColumn setup + NoiseSign + Y-loop for GenTerra column computation.
 * Direct port from VS 1.22.2 decompiled NewNormalizedSimplexFractalNoise.ColumnNoise.
 */
#pragma STDC FP_CONTRACT OFF

#include "tungsten_noise.h"
#include <math.h>
#include <string.h>

#define VALUE_MULTIPLIER 1.1845758506756423
#define THRESHOLD_RESCALE 1.2000000000000002
#define AMP_FREQ_TO_SMOOTHING 3.5
#define MAX_OCTAVES 16

/* Internal per-column octave entry (same as VS ColumnNoise.OctaveEntry) */
typedef struct {
    int64_t seed;
    double x;
    double frequency_y;
    double z;
    double amplitude;
    double threshold;
    double smoothing_factor;
    double stop_bound;
} OctaveEntry;

/* Past evaluation cache (same as VS ColumnNoise.PastEvaluation) */
typedef struct {
    double value;
    double y;
} PastEval;

static inline double smooth_max(double a, double b, double smoothing)
{
    if (smoothing <= 0.0) return a > b ? a : b;
    double diff = a - b;
    if (diff > smoothing) return a;
    if (diff < -smoothing) return b;
    double t = (diff + smoothing) / (2.0 * smoothing);
    return b + t * t * smoothing;
}

static inline double smooth_min(double a, double b, double smoothing)
{
    return -smooth_max(-a, -b, smoothing);
}

static inline double apply_thresholding(double value, double threshold, double smoothing_factor)
{
    return smooth_max(0.0, value - threshold, smoothing_factor)
         + smooth_min(0.0, value + threshold, smoothing_factor);
}

static inline double noise_value_curve(double value)
{
    return value / sqrt(1.0 + value * value) * 0.5 + 0.5;
}

static inline double noise_value_curve_inverse(double value)
{
    if (value <= 0.0) return -1e308;
    if (value >= 1.0) return 1e308;
    value = value * 2.0 - 1.0;
    return value / sqrt(1.0 - value * value);
}

/* External: from tungsten_noise_simplex.c */
extern float tungsten_evaluate_improved_xz(int64_t seed, double x, double y, double z);

/*
 * NoiseSign: determines whether noise at a given Y produces solid or air.
 * Uses past evaluation cache for early-exit bounds estimation.
 * Direct port of VS ColumnNoise.NoiseSign.
 */
static double noise_sign(
    OctaveEntry* entries, PastEval* past_evals, int num_active,
    double y, double inverse_curved_thresholder)
{
    double num = inverse_curved_thresholder;
    double num2 = inverse_curved_thresholder;
    double num3 = inverse_curved_thresholder;

    /* Loop 1: bounds estimation from past evaluations */
    for (int i = 0; i < num_active; i++)
    {
        if (!(num3 <= 0.0) && !(num2 >= 0.0))
            break;

        OctaveEntry* e = &entries[i];
        if (num2 >= e->stop_bound)
            return num2;
        if (num3 <= 0.0 - e->stop_bound)
            return num3;

        double freq_y = y * e->frequency_y;
        double dist = fabs(past_evals[i].y - freq_y);
        num2 += apply_thresholding(
            fmax(-1.0, past_evals[i].value - dist * 5.0) * e->amplitude,
            e->threshold, e->smoothing_factor);
        num3 += apply_thresholding(
            fmin(1.0, past_evals[i].value + dist * 5.0) * e->amplitude,
            e->threshold, e->smoothing_factor);
    }

    /* Loop 2: actual noise evaluation */
    for (int j = 0; j < num_active; j++)
    {
        OctaveEntry* e = &entries[j];
        if (num >= e->stop_bound || num <= 0.0 - e->stop_bound)
            break;

        double y2 = y * e->frequency_y;
        double val = (double)tungsten_evaluate_improved_xz(e->seed, e->x, y2, e->z);
        past_evals[j].value = val;
        past_evals[j].y = y2;
        num += apply_thresholding(val * e->amplitude, e->threshold, e->smoothing_factor);
    }

    return num;
}

/*
 * tungsten_compute_column_noise: Full column Y-loop computation.
 * Replaces ForColumn + the Y-loop body from GenTerra.generate().
 */
TUNGSTEN_EXPORT int32_t tungsten_compute_column_noise(
    int32_t num_octaves,
    const int64_t* octave_seeds,
    const double* frequencies,
    double vertical_noise_rel_freq,
    double distorted_x,
    double distorted_z,
    const double* col_amplitudes,
    const double* col_thresholds,
    float y_displacement,
    const float* landform_weights,
    int32_t num_landforms,
    int32_t map_size_y,
    const float* terrain_y_thresholds_flat,
    int32_t taper_threshold,
    double geo_upheaval_amplitude,
    uint8_t* out_solidity_bits)
{
    /* Clear output */
    int bytes = (map_size_y + 7) / 8;
    memset(out_solidity_bits, 0, bytes);

    /* ForColumn equivalent: sort octaves by magnitude (largest first) */
    OctaveEntry entries[MAX_OCTAVES];
    PastEval past_evals[MAX_OCTAVES];
    double mags[MAX_OCTAVES];
    int order[MAX_OCTAVES];
    int num_active = 0;
    double total_mag = 0.0;

    for (int i = num_octaves - 1; i >= 0; i--)
    {
        mags[i] = fmax(0.0, fabs(col_amplitudes[i]) - col_thresholds[i]) * VALUE_MULTIPLIER;
        total_mag += mags[i];
        if (mags[i] != 0.0)
        {
            order[num_active] = i;
            /* Insertion sort */
            for (int j = num_active - 1; j >= 0; j--)
            {
                if (mags[order[j + 1]] > mags[order[j]])
                {
                    int tmp = order[j];
                    order[j] = order[j + 1];
                    order[j + 1] = tmp;
                }
            }
            num_active++;
        }
    }

    double bound_min = noise_value_curve(-total_mag);
    double bound_max = noise_value_curve(total_mag);

    /* Fill octave entries */
    double cum_mag = 0.0;
    for (int i = num_active - 1; i >= 0; i--)
    {
        int idx = order[i];
        cum_mag += mags[idx];
        double freq = frequencies[idx];
        entries[i].seed = octave_seeds[idx];
        entries[i].x = distorted_x * freq;
        entries[i].z = distorted_z * freq;
        entries[i].frequency_y = freq * vertical_noise_rel_freq;
        entries[i].amplitude = col_amplitudes[idx] * VALUE_MULTIPLIER;
        entries[i].threshold = col_thresholds[idx] * THRESHOLD_RESCALE;
        entries[i].smoothing_factor = col_amplitudes[idx] * freq * AMP_FREQ_TO_SMOOTHING;
        entries[i].stop_bound = cum_mag;
        past_evals[i].value = 0.0;
        past_evals[i].y = NAN; /* Forces full evaluation on first call */
    }

    int map_size_ym2 = map_size_y - 2;
    float y_slide = y_displacement - (float)(int)floor((double)y_displacement);
    int highest_solid = 0;

    /* Y-loop */
    for (int y = 1; y <= map_size_ym2; y++)
    {
        /* Compute displaced Y threshold from landform weights */
        float displaced_y = (float)y + y_displacement;
        int y_base = (int)floor((double)displaced_y);
        if (y_base < 0) y_base = 0;
        if (y_base > map_size_ym2) y_base = map_size_ym2;

        double threshold = 0.0;
        for (int m = 0; m < num_landforms; m++)
        {
            float w = landform_weights[m];
            if (w != 0.0f)
            {
                const float* lf_thresholds = terrain_y_thresholds_flat + m * (map_size_y + 1);
                float lerped = lf_thresholds[y_base] + (lf_thresholds[y_base + 1] - lf_thresholds[y_base]) * y_slide;
                threshold += (double)(w * lerped);
            }
        }

        /* Geo upheaval taper (only near world top) */
        if ((double)y > (double)taper_threshold && (double)y_displacement < -2.0)
        {
            double clamped_dist = fmax(fmin(-(double)y_displacement, (double)y - (double)map_size_y), (double)y);
            /* Actually the vanilla clamp is: Clamp(-distY, posY - mapSizeY, posY) */
            double neg_dist = -(double)y_displacement;
            if (neg_dist < (double)y - (double)map_size_y) neg_dist = (double)y - (double)map_size_y;
            if (neg_dist > (double)y) neg_dist = (double)y;
            double above_taper = (double)y - (double)taper_threshold;
            threshold += above_taper * neg_dist / (40.0 * geo_upheaval_amplitude);
        }

        /* Branch 1: threshold <= boundMin → solid */
        if (threshold <= bound_min)
        {
            out_solidity_bits[y >> 3] |= (uint8_t)(1 << (y & 7));
            highest_solid = y;
        }
        /* Branch 2: threshold >= boundMax → air, skip rest */
        else if (threshold >= bound_max)
        {
            break;
        }
        /* Branch 3: evaluate noise */
        else
        {
            double inv = -noise_value_curve_inverse(threshold);
            double result = noise_sign(entries, past_evals, num_active, (double)y, inv);
            if (result > 0.0)
            {
                out_solidity_bits[y >> 3] |= (uint8_t)(1 << (y & 7));
                highest_solid = y;
            }
        }
    }

    return highest_solid;
}
