using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockSharp.Algo;
using StockSharp.BusinessEntities;
using StockSharp.Logging;
using StockSharp.Messages;

namespace PrismaBoy
{
    sealed class CounterKick : MyBaseStrategy
    {
        private readonly decimal _kickPercent;                                          // Процент ударного дня
        private readonly TimeOfDay _timeOff;                                            // Время отсечки
        private readonly TimeOfDay _timeToStop;                                         // Время остановки стратегии

        private DateTime _nextTimeToPlaceOrdersIfKickDay;                               // Время следующей проверки на условия ударного дня

        public CounterKick(List<Security> securityList, Dictionary<string, decimal> securityVolumeDictionary, TimeSpan timeFrame, decimal stopLossPercent, decimal takeProfitPercent, decimal kickPercent, TimeOfDay timeOff, TimeOfDay timeToStop)
            : base(securityList, securityVolumeDictionary, timeFrame, stopLossPercent, takeProfitPercent)
        {
            Name = "Counter";
            IsIntraDay = true;
            TimeToStartRobot.Hours = 10;
            TimeToStartRobot.Minutes = 30;
            StopType = StopTypes.MarketLimitOffer;

            // В соответствии с параметрами конструктора
            _kickPercent = kickPercent;
            _timeOff = timeOff;
            _timeToStop = timeToStop;

            // Объявляем и инициализируем пустые переменные
            switch (DateTime.Today.DayOfWeek)
            {
                case DayOfWeek.Saturday:
                    _nextTimeToPlaceOrdersIfKickDay =
                        DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes).AddDays(2);
                    break;

                case DayOfWeek.Sunday:
                    _nextTimeToPlaceOrdersIfKickDay =
                        DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes).AddDays(1);
                    break;

                default:
                    _nextTimeToPlaceOrdersIfKickDay = DateTime.Today.AddHours(_timeOff.Hours).AddMinutes(_timeOff.Minutes);
                    break;
            }
        }

        /// <summary>
        /// Событие старта стратегии
        /// </summary>
        protected override void OnStarted()
        {
            TimeToStopRobot = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, _timeToStop.Hours,
                                           _timeToStop.Minutes, 5);

            // Подписываемся на события прихода времени отсечки
            Security
                .WhenTimeCome(_nextTimeToPlaceOrdersIfKickDay)
                .Do(PlaceOrdersIfCounterKickDay)
                .Once()
                .Apply(this);

            this.AddInfoLog("Стратегия запускается со следующими параметрами:" +
                            "\nВремя отсечки: " + _nextTimeToPlaceOrdersIfKickDay +
                            "\nУдар, %: " + _kickPercent +
                            "\nСтоплосс, %: " + StopLossPercent);

            var prevEveningPrices = MainWindow.Instance.ReadEveningPrices(1);
            var lastEveningInfo = SecurityList.Aggregate("Данные на вечерний клиринг предыдущего дня:\n\n",
                                                         (current, security) =>
                                                         current +
                                                         (security.Code + " - " + prevEveningPrices[security.Code].Price +
                                                          " at " +
                                                          prevEveningPrices[
                                                              security.Code
                                                              ].Time.ToString(
                                                                  CultureInfo.
                                                                      CurrentCulture) +
                                                          "\n"));

            this.AddInfoLog(lastEveningInfo);

            base.OnStarted();
        }

        /// <summary>
        /// Обработчик события прихода _timeOff времени и установки лимит ордера, если ударный день
        /// </summary>
        private void PlaceOrdersIfCounterKickDay()
        {
            if (MainWindow.Instance.ReadEveningPrices(1) == null || MainWindow.Instance.ReadEveningPrices(1).Count == 0)
            {
                this.AddInfoLog("ОШИБКА. Не удалось прочитать цены закрытия дневной торговой сессии или словарь цен не содержит ни одного инструмента.");
            }
            else
            {
                foreach (var security in SecurityList)
                {
                    if (!MainWindow.Instance.ReadEveningPrices(1).ContainsKey(security.Code))
                    {
                        this.AddInfoLog("ОШИБКА. Не удалось прочитать цены закрытия дневной торговой сессии для {0}",
                                        security.Code);
                        continue;
                    }

                    var prevEveningPrice = MainWindow.Instance.ReadEveningPrices(1)[security.Code].Price;
                    this.AddInfoLog("ЦЕНЫ:\nЗакрытие дневной торговой сессии {0} - {1}\n", security.Code,
                                    prevEveningPrice);


                    if (Math.Abs(security.LastTrade.Price - prevEveningPrice) <= prevEveningPrice * _kickPercent / 100)
                        continue;

                    this.AddInfoLog("ВХОД: цена вечернего клиринга - {0}, цена последней сделки - {1}.",
                                    prevEveningPrice, security.LastTrade.Price.ToString(CultureInfo.InvariantCulture));

                    // Если цена не равна нулю
                    if (security.LastTrade.Price != 0)
                    {
                        // Если последняя сделка была не ниже чем вечерний клиринг на соответствующее количество процентов
                        if (security.LastTrade.Price >= prevEveningPrice * (1 + _kickPercent / 100))
                        {
                            // Регистрируем ордер на продажу
                            var counterKickDayOrder = new Order
                            {
                                Comment = Name + ", enter",
                                Type = OrderTypes.Limit,
                                Volume = SecurityVolumeDictionary[security.Code],
                                Price = security.LastTrade.Price,
                                Portfolio = Portfolio,
                                Security = security,
                                Direction = Sides.Sell,
                            };

                            var stopPrice =
                                security.ShrinkPrice(Math.Round((security.LastTrade.Price) * (1 + StopLossPercent / 100)));

                            this.AddInfoLog(
                                "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку 'ПротивоУдарный день' на {1} по цене {2} c объемом {3} - стоп на {4}",
                                security.Code,
                                counterKickDayOrder.Direction == Sides.Sell ? "продажу" : "покупку",
                                counterKickDayOrder.Price.ToString(CultureInfo.InvariantCulture),
                                counterKickDayOrder.Volume.ToString(CultureInfo.InvariantCulture), stopPrice);

                            RegisterOrder(counterKickDayOrder);
                        }
                        // Если последняя сделка была не выше чем вечерний клиринг на соответствующее количество процентов
                        else if (security.LastTrade.Price <= prevEveningPrice * (1 - _kickPercent / 100))
                        {
                            var counterKickDayOrder = new Order
                            {
                                // Регистрируем ордер на покупку
                                Comment = Name + ", enter",
                                Type = OrderTypes.Limit,
                                Volume = SecurityVolumeDictionary[security.Code],
                                Price = security.LastTrade.Price,
                                Portfolio = Portfolio,
                                Security = security,
                                Direction = Sides.Buy,
                            };

                            var stopPrice =
                                security.ShrinkPrice(Math.Round((security.LastTrade.Price) * (1 - StopLossPercent / 100)));

                            this.AddInfoLog(
                                "ЗАЯВКА на ВХОД - {0}. Регистрируем заявку 'ПротивоУдарный день' на {1} по цене {2} c объемом {3} - стоп на {4}",
                                security.Code,
                                counterKickDayOrder.Direction == Sides.Sell ? "продажу" : "покупку",
                                counterKickDayOrder.Price.ToString(CultureInfo.InvariantCulture),
                                counterKickDayOrder.Volume.ToString(CultureInfo.InvariantCulture), stopPrice);

                            RegisterOrder(counterKickDayOrder);
                        }
                    }
                    else
                    {
                        this.AddInfoLog("Цена последней сделки по {0} почему-то равна 0... Игнорируем сигнал на вход.", security.Code);
                    }

                }
            }

            switch (_nextTimeToPlaceOrdersIfKickDay.AddDays(1).DayOfWeek)
            {
                case (DayOfWeek.Saturday):
                    _nextTimeToPlaceOrdersIfKickDay = _nextTimeToPlaceOrdersIfKickDay.AddDays(3);
                    break;

                case (DayOfWeek.Sunday):
                    _nextTimeToPlaceOrdersIfKickDay = _nextTimeToPlaceOrdersIfKickDay.AddDays(2);
                    break;

                default:
                    _nextTimeToPlaceOrdersIfKickDay = _nextTimeToPlaceOrdersIfKickDay.AddDays(1);
                    break;
            }


            // Подписываемся на события прихода времени отсечки
            Security
                .WhenTimeCome(_nextTimeToPlaceOrdersIfKickDay)
                .Do(PlaceOrdersIfCounterKickDay)
                .Once()
                .Apply(this);

            this.AddInfoLog("Следующая попытка: {0}", _nextTimeToPlaceOrdersIfKickDay);
        }

        /// <summary>
        /// Метод "выхода по рынку". Специальный, для CounterKick. Чтобы выходит не совсем по любой цене.
        /// </summary>
        protected override void ExitAtMarket(Security security)
        {
            var currentPosition = GetCurrentPosition(security);
            if (currentPosition == 0)
                return;

            var volume = Math.Abs(currentPosition);

            var newExitOrder = new Order
            {
                Comment = Name + ",m",
                Type = OrderTypes.Limit,
                Portfolio = Portfolio,
                Security = security,
                Volume = volume,
                Direction = currentPosition > 0 ? Sides.Sell : Sides.Buy,
                Price = currentPosition < 0
                        ? security.ShrinkPrice(security.BestBid.Price * (1 + 0.001m))
                        : security.ShrinkPrice(security.BestAsk.Price * (1 - 0.001m)),
            };

            // После срабатывания временного ордера, выводим сообщение в лог и останавливаем защитную стратегию

            newExitOrder
                .WhenRegistered()
                .Once()
                .Do(() => this.AddInfoLog(
                        "ВЫХОД по ВРЕМЕНИ - {0}. Зарегистрирована заявка на выход из позиции впереди лучшей цены {1} в стакане.",
                        security,
                        newExitOrder.Direction == Sides.Buy ? "Bid" : "Ask"))
                .Apply(this);

            newExitOrder
                .WhenMatched()
                .Do(() =>
                {
                    ActiveTrades = ActiveTrades.Where(activeTrade => activeTrade.Security != security.Code).ToList();

                    // Вызываем событие прихода изменения ActiveTrades
                    OnActiveTradesChanged(new ActiveTradesChangedEventArgs());

                    this.AddInfoLog(
                        "ВЫХОД по ВРЕМЕНИ - {0}. Вышли из позиции впереди лучшей цены {1} в стакане.",
                        security.Code,
                        newExitOrder.Direction == Sides.Buy ? "Bid" : "Ask");
                })
                .Apply(this);

            // Регистрируем ордер
            this.AddInfoLog("Регистрируем ордер на выход по ВРЕМЕНИ");
            RegisterOrder(newExitOrder);
        }
    }
}
