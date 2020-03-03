using LiveCharts.Geared;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HeartMonitorWPF
{
    public class SpeedTestVm : INotifyPropertyChanged
    {
        private double _trend = 80;
        private double _count;
        private double _currentLecture;
        private bool _isHot;

        public SpeedTestVm()
        {
            Values = new GearedValues<double>().WithQuality(Quality.Highest);
            ReadCommand = new RelayCommand(Read);
            StopCommand = new RelayCommand(Stop);
            CleaCommand = new RelayCommand(Clear);
            Values.AddRange(Enumerable.Repeat(_trend, Buffer));
            ReadCommand.Execute(null);
        }

        public bool IsReading { get; set; }
        public RelayCommand ReadCommand { get; set; }
        public RelayCommand StopCommand { get; set; }
        public RelayCommand CleaCommand { get; set; }
        public GearedValues<double> Values { get; set; }

        public int Buffer { get; } = 10240;

        private string _flyoutMessage;
        public string FlyoutMessage
        {
            get => _flyoutMessage;
            set
            {
                FlyoutOpen = true;
                _flyoutMessage = value;
                OnPropertyChanged("FlyoutOpen");
                OnPropertyChanged("FlyoutMessage");
            }
        }
        public bool FlyoutOpen { get; set; } = false;

        public double Count
        {
            get { return _count; }
            set
            {
                _count = value;
                OnPropertyChanged("Count");
            }
        }

        public double CurrentLecture
        {
            get { return _currentLecture; }
            set
            {
                _currentLecture = value;
                OnPropertyChanged("CurrentLecture");
            }
        }

        public bool IsHot
        {
            get { return _isHot; }
            set
            {
                var changed = value != _isHot;
                _isHot = value;
                if (changed) OnPropertyChanged("IsHot");
            }
        }

        private void Stop()
        {
            IsReading = false;
            FlyoutMessage = "STOPPED";
        }

        private void Clear()
        {
            Values.Clear();
            FlyoutMessage = "CLEARED";
        }

        private void Read()
        {
            FlyoutMessage = "READING";
            if (IsReading) return;

            //lets keep in memory only the last 20000 records,
            //to keep everything running faster
            IsReading = true;

            Action readFromTread = () =>
            {
                while (IsReading)
                {
                    Thread.Sleep(100);

                    var r = new Random();
                    _trend += (r.NextDouble() < 0.02 ? 1 : 0) * (r.NextDouble() < 0.5 ? 1 : -1);

                    var first = Values.DefaultIfEmpty(0).FirstOrDefault();
                    if (Values.Count > Buffer - 1) Values.Remove(first);
                    if (Values.Count < Buffer) Values.Add(_trend);
                    IsHot = _trend > 100;
                    Count = Values.Count;
                    CurrentLecture = _trend;
                }
            };

            Task.Factory.StartNew(readFromTread);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
