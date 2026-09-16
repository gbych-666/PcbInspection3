using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.WPF;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace PcbInspection.Pages
{
    /// <summary>
    /// 数据查询页面
    /// </summary>
    public partial class DataPage : Page
    {
        // =====================================================
        // 统计数据
        // =====================================================
        private class DailyStatistics
        {
            public string Date { get; set; }

            public int TotalCount { get; set; }

            public int PassCount { get; set; }

            public int NgCount { get; set; }

            public double PassRate
            {
                get
                {
                    if (TotalCount <= 0)
                        return 0;

                    return (double)PassCount /
                           TotalCount *
                           100.0;
                }
            }
        }
        // =====================================================
        // 统计文件根目录
        // =====================================================
        private string StatisticsRootDirectory =>
            Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Data");


        // =====================================================
        // 构造函数
        // =====================================================
        public DataPage()
        {
            InitializeComponent();

            InitializeQueryControls();

            // 默认查询今天
            DatePickerQuery.SelectedDate = DateTime.Today;

            // 默认查询当天
            Loaded += DataPage_Loaded;
        }


        // =====================================================
        // 页面加载
        // =====================================================
        private void DataPage_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                // 默认年份
                CmbYear.SelectedItem =
                    DateTime.Today.Year;

                // 默认月份
                CmbMonth.SelectedItem =
                    DateTime.Today.Month;

                // 默认显示当天
                QueryDay(DateTime.Today);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"加载数据页面失败：{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        // =====================================================
        // 初始化年份 / 月份
        // =====================================================
        private void InitializeQueryControls()
        {
            CmbYear.Items.Clear();
            int currentYear = DateTime.Now.Year;
            // 最近 10 年
            for (int year = currentYear - 9; year <= currentYear; year++)
            {
                CmbYear.Items.Add(year);
            }
            CmbMonth.Items.Clear();
            for (int month = 1; month <= 12; month++)
            {
                CmbMonth.Items.Add(month);
            }
        }
        // =====================================================
        // 查询当天按钮
        // =====================================================
        private void BtnQueryDay_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (!DatePickerQuery.SelectedDate.HasValue)
            {
                MessageBox.Show(
                    "请选择查询日期！",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            DateTime date =  DatePickerQuery.SelectedDate.Value.Date;
            QueryDay(date);
        }


        // =====================================================
        // 查询当天
        // =====================================================
        private void QueryDay(DateTime date)
        {
            try
            {
                DailyStatistics statistics =  LoadDailyStatistics(date);

                // =================================================
                // 更新顶部统计
                // =================================================
                TxtQueryTitle.Text = "当天统计";

                TxtQueryDate.Text =
                    date.ToString("yyyy-MM-dd");

                TxtQueryTotal.Text =
                    statistics.TotalCount.ToString();

                TxtQueryPass.Text =
                    statistics.PassCount.ToString();

                TxtQueryNg.Text =
                    statistics.NgCount.ToString();

                TxtQueryRate.Text =
                    $"{statistics.PassRate:F2}%";


                // =================================================
                // 更新饼图
                // =================================================
                TxtPieTitle.Text =
                    $"{date:yyyy-MM-dd} 良品 / 不良品";

                PieChartResult.Series =
                    new ISeries[]
                    {
                        new PieSeries<int>
                        {
                            Name = "良品",
                            Values = new[]
                            {
                                statistics.PassCount
                            },
                            DataLabelsPaint =
                                new LiveChartsCore.SkiaSharpView.Painting.SolidColorPaint(
                                    new SkiaSharp.SKColor(
                                        255,
                                        255,
                                        255)),
                            DataLabelsSize = 14
                        },

                        new PieSeries<int>
                        {
                            Name = "不良品",
                            Values = new[]
                            {
                                statistics.NgCount
                            },
                            DataLabelsPaint =
                                new LiveChartsCore.SkiaSharpView.Painting.SolidColorPaint(
                                    new SkiaSharp.SKColor(
                                        255,
                                        255,
                                        255)),
                            DataLabelsSize = 14
                        }
                    };


                // =================================================
                // 当天查询不需要月度曲线
                // 清空曲线
                // =================================================
                MonthLineChart.Series =
                    Array.Empty<ISeries>();

                MonthLineChart.XAxes =
                    Array.Empty<Axis>();

                MonthLineChart.YAxes =
                    Array.Empty<Axis>();

                TxtLineTitle.Text =
                    "当天查询不显示月度趋势";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"查询当天数据失败：{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        // =====================================================
        // 查询月份按钮
        // =====================================================
        private void BtnQueryMonth_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (CmbYear.SelectedItem == null ||
                CmbMonth.SelectedItem == null)
            {
                MessageBox.Show(
                    "请选择年份和月份！",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                return;
            }

            int year =
                Convert.ToInt32(
                    CmbYear.SelectedItem);

            int month =
                Convert.ToInt32(
                    CmbMonth.SelectedItem);

            QueryMonth(year, month);
        }


        // =====================================================
        // 查询整个月
        // =====================================================
        private void QueryMonth(
            int year,
            int month)
        {
            try
            {
                int daysInMonth =
                    DateTime.DaysInMonth(
                        year,
                        month);


                // =================================================
                // 每天数据
                // =================================================
                List<DailyStatistics> dailyList =
                    new List<DailyStatistics>();


                for (int day = 1;
                     day <= daysInMonth;
                     day++)
                {
                    DateTime date =
                        new DateTime(
                            year,
                            month,
                            day);

                    DailyStatistics statistics =
                        LoadDailyStatistics(date);

                    dailyList.Add(statistics);
                }


                // =================================================
                // 月度汇总
                // =================================================
                int totalCount =
                    dailyList.Sum(x => x.TotalCount);

                int passCount =
                    dailyList.Sum(x => x.PassCount);

                int ngCount =
                    dailyList.Sum(x => x.NgCount);

                double passRate =
                    totalCount <= 0
                        ? 0
                        : (double)passCount /
                          totalCount *
                          100.0;


                // =================================================
                // 更新顶部统计
                // =================================================
                TxtQueryTitle.Text =
                    "月份汇总";

                TxtQueryDate.Text =
                    $"{year}-{month:00}";

                TxtQueryTotal.Text =
                    totalCount.ToString();

                TxtQueryPass.Text =
                    passCount.ToString();

                TxtQueryNg.Text =
                    ngCount.ToString();

                TxtQueryRate.Text =
                    $"{passRate:F2}%";


                // =================================================
                // 更新饼图
                // =================================================
                TxtPieTitle.Text =
                    $"{year}-{month:00} 月度良品 / 不良品";

                PieChartResult.Series =
                    new ISeries[]
                    {
                        new PieSeries<int>
                        {
                            Name = "良品",
                            Values = new[]
                            {
                                passCount
                            }
                        },

                        new PieSeries<int>
                        {
                            Name = "不良品",
                            Values = new[]
                            {
                                ngCount
                            }
                        }
                    };
                // =================================================
                // 月度曲线
                // =================================================
                UpdateMonthLineChart( year, month, dailyList);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"查询月份数据失败：{ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        // =====================================================
        // 更新月度曲线图
        // =====================================================
        private void UpdateMonthLineChart( int year, int month, List<DailyStatistics> dailyList)
        {
            int days = dailyList.Count;
            // =================================================
            // X轴日期
            // =================================================
            string[] labels = Enumerable .Range(1, days)  .Select(x => $"{x}日") .ToArray();
            // =================================================
            // 良品数量
            // =================================================
            double[] passValues = dailyList .Select(x => (double)x.PassCount) .ToArray();
            // =================================================
            // 不良数量
            // =================================================
            double[] ngValues = dailyList .Select(x => (double)x.NgCount)  .ToArray();
            // =================================================
            // 良品率
            // =================================================
            double[] passRateValues = dailyList .Select(x => x.PassRate) .ToArray();
            // =================================================
            // 三条曲线
            // =================================================
            MonthLineChart.Series =
                new ISeries[]
                {
                    // 良品数
                    new LineSeries<double>
                    {
                        Name = "良品数",
                        Values = passValues,
                        GeometrySize = 7,
                        LineSmoothness = 0.3
                    },
                    // 不良品数
                    new LineSeries<double>
                    {
                        Name = "不良数",
                        Values = ngValues,
                        GeometrySize = 7,
                        LineSmoothness = 0.3
                    },
                    // 良品率
                    new LineSeries<double>
                    {
                        Name = "良率",
                        Values = passRateValues,
                        GeometrySize = 7,
                        LineSmoothness = 0.3,
                        ScalesYAt = 1
                    }
                };
            // =================================================
            // X轴
            // =================================================
            MonthLineChart.XAxes =
                new Axis[]
                {
                    new Axis
                    {
                        Labels = labels,
                        LabelsRotation = 0,
                        MinStep = 1
                    }
                };
            // =================================================
            // 左Y轴：数量
            // =================================================
            MonthLineChart.YAxes =
                new Axis[]
                {
                    new Axis
                    {
                        Name = "检测数量",
                        MinLimit = 0
                    },
                    // =================================================
                    // 右Y轴：良率
                    // =================================================
                    new Axis
                    {
                        Name = "良品率 (%)",
                        MinLimit = 0,
                        MaxLimit = 100
                    }
                };
            TxtLineTitle.Text =  $"{year}-{month:00} 每日检测趋势";
        }
        // =====================================================
        // 读取某一天统计数据
        // =====================================================
        private DailyStatistics LoadDailyStatistics( DateTime date)
        {
            try
            {
                string yearStr = date.ToString("yyyy");
                string dateStr = date.ToString("MMdd");
                string targetDir = Path.Combine( StatisticsRootDirectory,  yearStr, dateStr);
                string filePath = Path.Combine(  targetDir, "statistics.json");
                // =================================================
                // 没有数据
                // =================================================
                if (!File.Exists(filePath))
                {
                    return new DailyStatistics
                    {
                        Date = date.ToString("yyyy-MM-dd"),
                        TotalCount = 0,
                        PassCount = 0,
                        NgCount = 0
                    };
                }
                // =================================================
                // 读取JSON
                // =================================================
                string json = File.ReadAllText(filePath);
                DailyStatistics statistics = JsonSerializer.Deserialize<DailyStatistics>( json);
                if (statistics == null)
                {
                    return new DailyStatistics
                    {
                        Date = date.ToString("yyyy-MM-dd")
                    };
                }
                return statistics;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine( $"读取 {date:yyyy-MM-dd} 统计失败：{ex.Message}");
                return new DailyStatistics
                {
                    Date = date.ToString("yyyy-MM-dd"),
                    TotalCount = 0,
                    PassCount = 0,
                    NgCount = 0
                };
            }
        }
    }
}
 