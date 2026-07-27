/* Shared Chart.js setup for Pune Metro LMS:
   - DM Sans typography
   - no gridlines on any axis
   - data labels on every chart (chartjs-plugin-datalabels) */
Chart.register(ChartDataLabels);

Chart.defaults.font.family = "'DM Sans', sans-serif";
Chart.defaults.color = '#8094ae';

// Remove gridlines everywhere (keep the axis line hidden too)
Chart.defaults.scale.grid.display = false;
Chart.defaults.scale.border = Chart.defaults.scale.border || {};
Chart.defaults.scale.border.display = false;

// Data labels: value above bars/points; hide zeros to reduce noise
Chart.defaults.set('plugins.datalabels', {
    color: '#526484',
    font: { weight: '700', size: 11 },
    anchor: 'end',
    align: 'top',
    offset: 2,
    clamp: true,
    formatter: function (v) { return v === 0 || v === null ? '' : v; }
});

// For doughnut/pie: white labels centred in the slices
const LMS_DONUT_LABELS = {
    color: '#fff',
    anchor: 'center',
    align: 'center',
    font: { weight: '700', size: 12 },
    formatter: function (v) { return v === 0 ? '' : v; }
};
