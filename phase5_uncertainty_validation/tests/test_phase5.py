import numpy as np
import pandas as pd
import pytest

from phase5_uncertainty_validation import io
from phase5_uncertainty_validation import metrics


def make_run(run_id: str, group_id: str, mean_vec: np.ndarray, cov: np.ndarray) -> io.LoadedRun:
    windows = pd.DataFrame({
        "m1": [mean_vec[0]],
        "m2": [mean_vec[1]],
        "m3": [mean_vec[2]],
    })
    cov_mats = np.array([cov])
    return io.LoadedRun(
        run_id=run_id,
        covariance_group_id=group_id,
        windows=windows,
        cov_mats=cov_mats,
        derived={},
        warnings={},
    )


def test_covariance_symmetrization():
    df = pd.DataFrame({
        "m1": [1.0],
        "m2": [2.0],
        "m3": [3.0],
        "Cov_m_00": [1.0],
        "Cov_m_01": [0.2],
        "Cov_m_02": [0.1],
        "Cov_m_10": [0.3],
        "Cov_m_11": [1.5],
        "Cov_m_12": [0.0],
        "Cov_m_20": [0.05],
        "Cov_m_21": [0.02],
        "Cov_m_22": [2.0],
    })
    cov, warnings, mask = io.parse_covariance(df)
    assert mask.all()
    assert warnings["symmetrized"] > 0
    assert np.allclose(cov, cov.transpose(0, 2, 1))


def test_delta_method_matches_finite_difference():
    m1, m2 = 2.0, 4.5
    base = np.array([m1, m2, 0.0])
    cov = np.array([
        [0.2, 0.05, 0.0],
        [0.05, 0.3, 0.0],
        [0.0, 0.0, 0.1],
    ])
    var_delta = metrics.propagate_y_variance(base, cov)
    eps = 1e-5
    grads = []
    for i in range(2):
        delta = np.zeros_like(base)
        delta[i] = eps
        y_plus = metrics.feynman_y(np.array([base[0] + delta[0]]), np.array([base[1] + delta[1]]))[0]
        y_minus = metrics.feynman_y(np.array([base[0] - delta[0]]), np.array([base[1] - delta[1]]))[0]
        grads.append((y_plus - y_minus) / (2 * eps))
    grad_vec = np.array([grads[0], grads[1], 0.0])
    var_fd = float(grad_vec.T @ cov @ grad_vec)
    assert np.isclose(var_delta, var_fd, rtol=1e-3, atol=1e-6)


def test_coverage_on_synthetic_normal():
    rng = np.random.default_rng(0)
    mu = np.array([1.0, 2.0, 3.0])
    cov = np.array([
        [0.2, 0.0, 0.0],
        [0.0, 0.3, 0.0],
        [0.0, 0.0, 0.4],
    ])
    runs = [make_run(str(i), "g1", rng.multivariate_normal(mu, cov), cov) for i in range(200)]
    cov_pred = [cov for _ in runs]
    coverage = metrics.coverage_for_group("g1", runs, cov_pred, 0.95, "m1")
    assert 0.90 <= coverage.coverage_full <= 0.99


def test_whitened_residuals_identity_covariance():
    rng = np.random.default_rng(1)
    mu = np.array([0.0, 0.0, 0.0])
    cov = np.array([
        [1.0, 0.2, 0.1],
        [0.2, 1.5, 0.3],
        [0.1, 0.3, 0.8],
    ])
    runs = [make_run(str(i), "g1", rng.multivariate_normal(mu, cov), cov) for i in range(500)]
    cov_pred = [cov for _ in runs]
    whitened = metrics.whitened_residuals(runs, cov_pred, mu)
    cov_white = np.cov(whitened.T)
    assert np.allclose(cov_white, np.eye(3), atol=0.1)


def test_reduced_count_subsampling_preserves_mean():
    rng = np.random.default_rng(2)
    windows = pd.DataFrame({
        "m1": rng.poisson(5, size=1000),
        "m2": rng.poisson(10, size=1000),
        "m3": rng.poisson(20, size=1000),
    })
    cov = np.eye(3)
    run = io.LoadedRun(
        run_id="r1",
        covariance_group_id="g1",
        windows=windows,
        cov_mats=np.repeat(cov[None, :, :], len(windows), axis=0),
        derived={},
        warnings={},
    )
    reduced = metrics.reduced_count_runs([run], 0.5, rng)[0]
    assert abs(reduced.windows["m1"].mean() - windows["m1"].mean()) < 0.5


def test_discover_run_file_prefers_largest_ndjson(tmp_path):
    run_dir = tmp_path / "run-1"
    run_dir.mkdir()
    small = run_dir / "windows.ndjson"
    large = run_dir / "rt_windows.ndjson"
    small.write_text('{"m1":1}\n', encoding="utf-8")
    large.write_text('{"m1":1}\n{"m1":2}\n', encoding="utf-8")
    chosen = io.discover_run_file(tmp_path, "run-1")
    assert chosen == large


def test_load_window_data_ndjson_with_blank_lines(tmp_path):
    path = tmp_path / "windows.ndjson"
    path.write_text('\n{"m1":1}\n\n{"m1":2}\n', encoding="utf-8")
    df = io.load_window_data(path)
    assert df["m1"].tolist() == [1, 2]


def test_load_window_data_json_array_fallback(tmp_path):
    path = tmp_path / "windows.json"
    path.write_text('[{"m1":1},{"m1":2}]', encoding="utf-8")
    df = io.load_window_data(path)
    assert df["m1"].tolist() == [1, 2]


def test_load_window_data_empty_file(tmp_path):
    path = tmp_path / "windows.ndjson"
    path.write_text("", encoding="utf-8")
    with pytest.raises(ValueError, match="Window data file is empty"):
        io.load_window_data(path)
