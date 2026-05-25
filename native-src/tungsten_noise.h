/*
 * tungsten_noise.h
 * Native noise computation for Tungsten mod (Vintage Story)
 * Phase C1: Y-loop + NoiseSign + Evaluate_ImprovedXZ
 */
#ifndef TUNGSTEN_NOISE_H
#define TUNGSTEN_NOISE_H

#include <stdint.h>

#ifdef _WIN32
  #define TUNGSTEN_EXPORT __declspec(dllexport)
#else
  #define TUNGSTEN_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Single-point noise evaluation (for validation) */
TUNGSTEN_EXPORT float tungsten_evaluate_improved_xz(int64_t seed, double x, double y, double z);

/* Full column noise computation (Phase C1 entry point)
 * Computes solidity bits for one column (all Y levels).
 * Returns highest solid Y level.
 */
TUNGSTEN_EXPORT int32_t tungsten_compute_column_noise(
    /* Terrain noise config */
    int32_t num_octaves,
    const int64_t* octave_seeds,
    const double* frequencies,
    double vertical_noise_rel_freq,
    /* Per-column distorted coordinates */
    double distorted_x,
    double distorted_z,
    /* Per-column BiLerped octave params */
    const double* col_amplitudes,
    const double* col_thresholds,
    /* Y displacement from upheaval+ocean */
    float y_displacement,
    /* Landform thresholds (flattened) */
    const float* landform_weights,
    int32_t num_landforms,
    int32_t map_size_y,
    const float* terrain_y_thresholds_flat,  /* [num_landforms * (map_size_y + 1)] */
    /* Geo upheaval taper */
    int32_t taper_threshold,
    double geo_upheaval_amplitude,
    /* Output (caller-allocated) */
    uint8_t* out_solidity_bits   /* [(map_size_y + 7) / 8] bytes */
);

#ifdef __cplusplus
}
#endif

#endif /* TUNGSTEN_NOISE_H */
