﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerFx.Core.IR;
using Microsoft.PowerFx.Core.Localization;
using Microsoft.PowerFx.Core.Utils;
using Microsoft.PowerFx.Types;

namespace Microsoft.PowerFx.Functions
{
    // Due to .Net static ctor initialization, must place in a separate class from Library. 
    internal static class LibraryFlags
    {
        public static readonly RegexOptions RegExFlags = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    }

    internal static partial class Library
    {
        private static readonly RegexOptions RegExFlags = LibraryFlags.RegExFlags;

        private static readonly Regex _ampmReplaceRegex = new Regex("[aA][mM]\\/[pP][mM]", RegExFlags);
        private static readonly Regex _apReplaceRegex = new Regex("[aA]\\/[pP]", RegExFlags);
        private static readonly Regex _minutesBeforeSecondsRegex = new Regex("[mM][^dDyYhH]+[sS]", RegExFlags);
        private static readonly Regex _minutesAfterHoursRegex = new Regex("[hH][^dDyYmM]+[mM]", RegExFlags);
        private static readonly Regex _minutesRegex = new Regex("[mM]", RegExFlags);
        private static readonly Regex _internalStringRegex = new Regex("([\"][^\"]*[\"])", RegExFlags);
        private static readonly Regex _daysDetokenizeRegex = new Regex("[\u0004][\u0004][\u0004][\u0004]+", RegExFlags);
        private static readonly Regex _monthsDetokenizeRegex = new Regex("[\u0003][\u0003][\u0003][\u0003]+", RegExFlags);
        private static readonly Regex _yearsDetokenizeRegex = new Regex("[\u0005][\u0005][\u0005]+", RegExFlags);
        private static readonly Regex _years2DetokenizeRegex = new Regex("[\u0005]+", RegExFlags);
        private static readonly Regex _hoursDetokenizeRegex = new Regex("[\u0006][\u0006]+", RegExFlags);
        private static readonly Regex _minutesDetokenizeRegex = new Regex("[\u000A][\u000A]+", RegExFlags);
        private static readonly Regex _secondsDetokenizeRegex = new Regex("[\u0008][\u0008]+", RegExFlags);
        private static readonly Regex _milisecondsDetokenizeRegex = new Regex("[\u000e]+", RegExFlags);

        // Char is used for PA string escaping 
        public static FormulaValue Char(IRContext irContext, NumberValue[] args)
        {
            var arg0 = args[0];

            if (arg0.Value < 1 || arg0.Value >= 256)
            {
                return CommonErrors.InvalidCharValue(irContext);
            }

            var str = new string((char)arg0.Value, 1);
            return new StringValue(irContext, str);
        }

        public static async ValueTask<FormulaValue> Concat(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, FormulaValue[] args)
        {
            // Streaming 
            var arg0 = (TableValue)args[0];
            var arg1 = (LambdaFormulaValue)args[1];
            var separator = args.Length > 2 ? ((StringValue)args[2]).Value : string.Empty;

            var sb = new StringBuilder();
            var first = true;

            foreach (var row in arg0.Rows)
            {
                runner.CheckCancel();

                if (first)
                {
                    first = false;
                }
                else
                {
                    sb.Append(separator);
                }

                SymbolContext childContext;
                if (row.IsValue)
                {
                    childContext = context.SymbolContext.WithScopeValues(row.Value);
                }
                else if (row.IsBlank)
                {
                    childContext = context.SymbolContext.WithScopeValues(RecordValue.Empty());
                }
                else
                {
                    childContext = context.SymbolContext.WithScopeValues(row.Error);
                }

                var result = await arg1.EvalInRowScopeAsync(context.NewScope(childContext)).ConfigureAwait(false);

                string str;
                if (result is ErrorValue ev)
                {
                    return ev;
                }
                else if (result is BlankValue)
                {
                    str = string.Empty;
                }
                else
                {
                    str = ((StringValue)result).Value;
                }

                sb.Append(str);
            }

            return new StringValue(irContext, sb.ToString());
        }

        // Scalar
        // Operator & maps to this function call.
        public static FormulaValue Concatenate(IRContext irContext, StringValue[] args)
        {
            var sb = new StringBuilder();

            foreach (var arg in args)
            {
                sb.Append(arg.Value);
            }

            return new StringValue(irContext, sb.ToString());
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-value
        // Convert string to number
        public static FormulaValue Value(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, FormulaValue[] args)
        {
            return Value(CreateFormattingInfo(runner), irContext, args);
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-value
        // Convert string to number
        public static FormulaValue Value(FormattingInfo formatInfo, IRContext irContext, FormulaValue[] args)
        {
            if (irContext.ResultType is DecimalType)
            {
                return Decimal(formatInfo, irContext, args);
            }
            else
            {
                return Float(formatInfo, irContext, args);
            }
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-value
        // Convert string to number
        public static FormulaValue Float(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, FormulaValue[] args)
        {
            return Float(CreateFormattingInfo(runner), irContext, args);
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-value
        // Convert string to number
        public static FormulaValue Float(FormattingInfo formatInfo, IRContext irContext, FormulaValue[] args)
        {
            if (args[0] is StringValue sv)
            {
                if (string.IsNullOrEmpty(sv.Value))
                {
                    return new BlankValue(irContext);
                }
            }

            // culture will have Cultural info in case one was passed in argument else it will have the default one.
            var culture = formatInfo.CultureInfo;
            if (args.Length > 1)
            {
                if (args[1] is StringValue cultureArg && !TryGetCulture(cultureArg.Value, out culture))
                {
                    return CommonErrors.BadLanguageCode(irContext, cultureArg.Value);
                }

                formatInfo.CultureInfo = culture;
            }

            bool isValue = TryFloat(formatInfo, irContext, args[0], out NumberValue result);

            return isValue ? result : CommonErrors.ArgumentOutOfRange(irContext);
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-value
        // Convert string to number
        public static bool TryFloat(FormattingInfo formatInfo, IRContext irContext, FormulaValue value, out NumberValue result)
        {
            result = null;

            Contract.Assert(NumberValue.AllowedListConvertToNumber.Contains(value.Type));

            switch (value)
            {
                case NumberValue n:
                    result = n;
                    break;
                case DecimalValue w:
                    result = DecimalToNumber(irContext, w);
                    break;
                case BooleanValue b:
                    result = BooleanToNumber(irContext, b);
                    break;
                case DateValue dv:
                    result = DateToNumber(formatInfo, irContext, dv);
                    break;
                case DateTimeValue dtv:
                    result = DateTimeToNumber(formatInfo, irContext, dtv);
                    break;
                case StringValue sv:
                    var (val, err) = ConvertToNumber(sv.Value, formatInfo.CultureInfo);

                    if (err == ConvertionStatus.Ok)
                    {
                        result = new NumberValue(irContext, val);
                    }

                    break;
            }

            return result != null;
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-value
        // Convert string to number
        public static FormulaValue Decimal(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, FormulaValue[] args)
        {
            return Decimal(CreateFormattingInfo(runner), irContext, args);
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-value
        // Convert string to number
        public static FormulaValue Decimal(FormattingInfo formatInfo, IRContext irContext, FormulaValue[] args)
        {
            if (args[0] is StringValue sv)
            {
                if (string.IsNullOrEmpty(sv.Value))
                {
                    return new BlankValue(irContext);
                }
            }

            // culture will have Cultural info in case one was passed in argument else it will have the default one.
            var culture = formatInfo.CultureInfo;
            if (args.Length > 1)
            {
                if (args[1] is StringValue cultureArg && !TryGetCulture(cultureArg.Value, out culture))
                {
                    return CommonErrors.BadLanguageCode(irContext, cultureArg.Value);
                }

                formatInfo.CultureInfo = culture;
            }

            bool isValue = TryDecimal(formatInfo, irContext, args[0], out DecimalValue result);

            return isValue ? result : CommonErrors.ArgumentOutOfRange(irContext);
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-value
        // Convert string to number
        public static bool TryDecimal(FormattingInfo formatInfo, IRContext irContext, FormulaValue value, out DecimalValue result)
        {
            result = null;

            Contract.Assert(DecimalValue.AllowedListConvertToDecimal.Contains(value.Type));

            switch (value)
            {
                case NumberValue n:
                    var (num, numErr) = ConvertNumberToDecimal(n.Value);
                    if (numErr == ConvertionStatus.Ok)
                    {
                        result = new DecimalValue(irContext, num);
                    }

                    break;
                case DecimalValue w:
                    result = w;
                    break;
                case BooleanValue b:
                    result = BooleanToDecimal(irContext, b);
                    break;
                case DateValue dv:
                    result = DateToDecimal(formatInfo, irContext, dv);
                    break;
                case DateTimeValue dtv:
                    result = DateTimeToDecimal(formatInfo, irContext, dtv);
                    break;
                case StringValue sv:
                    var (str, strErr) = ConvertToDecimal(sv.Value, formatInfo.CultureInfo);

                    if (strErr == ConvertionStatus.Ok)
                    {
                        result = new DecimalValue(irContext, str);
                    }

                    break;
            }

            return result != null;
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-text
        public static FormulaValue Text(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, FormulaValue[] args)
        {
            return Text(CreateFormattingInfo(runner), irContext, args);
        }

        public static FormulaValue Text(FormattingInfo formatInfo, IRContext irContext, FormulaValue[] args)
        {
            const int formatSize = 100;
            string formatString = null;

            if (args.Length > 1 && args[1] is StringValue fs)
            {
                formatString = fs.Value;
            }

            var culture = formatInfo.CultureInfo;
            if (args.Length > 2 && args[2] is StringValue languageCode)
            {
                if (!TryGetCulture(languageCode.Value, out culture))
                {
                    return CommonErrors.BadLanguageCode(irContext, languageCode.Value);
                }

                formatInfo.CultureInfo = culture;
            }

            // We limit the format string size
            if (formatString != null && formatString.Length > formatSize)
            {
                var customErrorMessage = StringResources.Get(TexlStrings.ErrTextFormatTooLarge, culture.Name);
                return CommonErrors.GenericInvalidArgument(irContext, string.Format(CultureInfo.InvariantCulture, customErrorMessage, formatSize));
            }

            if (formatString != null && !TextFormatUtils.IsValidFormatArg(formatString, out bool hasDateTimeFmt, out bool hasNumberFmt))
            {
                var customErrorMessage = StringResources.Get(TexlStrings.ErrIncorrectFormat_Func, culture.Name);
                return CommonErrors.GenericInvalidArgument(irContext, string.Format(CultureInfo.InvariantCulture, customErrorMessage, "Text"));
            }

            var isText = TryText(formatInfo, irContext, args[0], formatString, out StringValue result);

            return isText ? result : CommonErrors.GenericInvalidArgument(irContext, StringResources.Get(TexlStrings.ErrTextInvalidFormat, culture.Name));
        }

        public static bool TryText(FormattingInfo formatInfo, IRContext irContext, FormulaValue value, string formatString, out StringValue result)
        {
            var timeZoneInfo = formatInfo.TimeZoneInfo;
            var culture = formatInfo.CultureInfo;
            var hasDateTimeFmt = false;
            var hasNumberFmt = false;
            result = null;

            if (formatString != null && !TextFormatUtils.IsValidFormatArg(formatString, out hasDateTimeFmt, out hasNumberFmt))
            {
                return false;
            }

            Contract.Assert(StringValue.AllowedListConvertToString.Contains(value.Type));

            switch (value)
            {
                case StringValue sv:
                    result = sv;
                    break;
                case NumberValue num:
                    if (formatString != null && hasDateTimeFmt)
                    {
                        // It's a number, formatted as date/time. Let's convert it to a date/time value first
                        var newDateTime = Library.NumberToDateTime(formatInfo, IRContext.NotInSource(FormulaType.DateTime), num);

                        return TryExpandDateTimeExcelFormatSpecifiersToStringValue(irContext, formatString, "g", newDateTime.GetConvertedValue(timeZoneInfo), timeZoneInfo, culture, formatInfo.CancellationToken, out result);
                    }
                    else
                    {
                        result = new StringValue(irContext, num.Value.ToString(formatString ?? "g", culture));
                    }

                    break;

                case DecimalValue dec:
                    if (formatString != null && hasDateTimeFmt)
                    {
                        // It's a number, formatted as date/time. Let's convert it to a date/time value first
                        var decNum = new NumberValue(IRContext.NotInSource(FormulaType.Number), (double)dec.Value);
                        var newDateTime = Library.NumberToDateTime(formatInfo, IRContext.NotInSource(FormulaType.DateTime), decNum);
                        return TryExpandDateTimeExcelFormatSpecifiersToStringValue(irContext, formatString, "g", newDateTime.GetConvertedValue(timeZoneInfo), timeZoneInfo, culture, formatInfo.CancellationToken, out result);
                    }
                    else
                    {
                        var normalized = dec.Normalize();
                        result = new StringValue(irContext, normalized.ToString(formatString ?? "g", culture));
                    }

                    break;
                case DateTimeValue dateTimeValue:
                    if (formatString != null && hasNumberFmt)
                    {
                        // It's a datetime, formatted as number. Let's convert it to a number value first
                        var newNumber = Library.DateTimeToNumber(formatInfo, IRContext.NotInSource(FormulaType.Number), dateTimeValue);
                        result = new StringValue(irContext, newNumber.Value.ToString(formatString, culture));
                    }
                    else
                    {
                        return TryExpandDateTimeExcelFormatSpecifiersToStringValue(irContext, formatString, "g", dateTimeValue.GetConvertedValue(timeZoneInfo), timeZoneInfo, culture, formatInfo.CancellationToken, out result);
                    }

                    break;
                case DateValue dateValue:
                    if (formatString != null && hasNumberFmt)
                    {
                        NumberValue newDateNumber = Library.DateToNumber(formatInfo, IRContext.NotInSource(FormulaType.Number), dateValue) as NumberValue;
                        result = new StringValue(irContext, newDateNumber.Value.ToString(formatString, culture));
                    }
                    else
                    {
                        return TryExpandDateTimeExcelFormatSpecifiersToStringValue(irContext, formatString, "d", dateValue.GetConvertedValue(timeZoneInfo), timeZoneInfo, culture, formatInfo.CancellationToken, out result);
                    }

                    break;
                case TimeValue timeValue:
                    if (formatString != null && hasNumberFmt)
                    {
                        var newNumber = Library.TimeToNumber(IRContext.NotInSource(FormulaType.Number), new TimeValue[] { timeValue });
                        result = new StringValue(irContext, newNumber.Value.ToString(formatString, culture));
                    }
                    else
                    {
                        var dtValue = Library.TimeToDateTime(formatInfo, IRContext.NotInSource(FormulaType.DateTime), timeValue);
                        return TryExpandDateTimeExcelFormatSpecifiersToStringValue(irContext, formatString, "t", dtValue.GetConvertedValue(timeZoneInfo), timeZoneInfo, culture, formatInfo.CancellationToken, out result);
                    }

                    break;
                case BooleanValue b:
                    result = new StringValue(irContext, b.Value.ToString(culture).ToLowerInvariant());
                    break;
                case GuidValue g:
                    result = new StringValue(irContext, g.Value.ToString("d", CultureInfo.InvariantCulture));
                    break;
            }

            return result != null;
        }

        internal static bool TryExpandDateTimeExcelFormatSpecifiersToStringValue(IRContext irContext, string format, string defaultFormat, DateTime dateTime, TimeZoneInfo timeZoneInfo, CultureInfo culture, CancellationToken cancellationToken, out StringValue result)
        {
            result = null;
            if (format == null)
            {
                result = new StringValue(irContext, dateTime.ToString(defaultFormat, culture));
                return true;
            }

            // DateTime format
            switch (format.ToLowerInvariant())
            {
                case "'shortdatetime24'":
                case "'shortdatetime'":
                case "'shorttime24'":
                case "'shorttime'":
                case "'shortdate'":
                case "'longdatetime24'":
                case "'longdatetime'":
                case "'longtime24'":
                case "'longtime'":
                case "'longdate'":
                    var formatStr = ExpandDateTimeFormatSpecifiers(format, culture);
                    result = new StringValue(irContext, dateTime.ToString(formatStr, culture));
                    break;
                case "'utc'":
                case "utc":
                    var formatUtcStr = ExpandDateTimeFormatSpecifiers(format, culture);
                    result = new StringValue(irContext, ConvertToUTC(dateTime, timeZoneInfo).ToString(formatUtcStr, culture));
                    break;
                default:
                    try
                    {
                        var stringResult = ResolveDateTimeFormatAmbiguities(format, dateTime, culture, cancellationToken);
                        result = new StringValue(irContext, stringResult);
                    }
                    catch (FormatException)
                    {
                        return false;
                    }

                    break;
            }

            return result != null;
        }

        internal static string ExpandDateTimeFormatSpecifiers(string format, CultureInfo culture)
        {
            var info = DateTimeFormatInfo.GetInstance(culture);

            switch (format.ToLowerInvariant())
            {
                case "'shortdatetime24'":
                    // TODO: This might be wrong for some cultures
                    return ReplaceWith24HourClock(info.ShortDatePattern + " " + info.ShortTimePattern);
                case "'shortdatetime'":
                    // TODO: This might be wrong for some cultures
                    return info.ShortDatePattern + " " + info.ShortTimePattern;
                case "'shorttime24'":
                    return ReplaceWith24HourClock(info.ShortTimePattern);
                case "'shorttime'":
                    return info.ShortTimePattern;
                case "'shortdate'":
                    return info.ShortDatePattern;
                case "'longdatetime24'":
                    return ReplaceWith24HourClock(info.FullDateTimePattern);
                case "'longdatetime'":
                    return info.FullDateTimePattern;
                case "'longtime24'":
                    return ReplaceWith24HourClock(info.LongTimePattern);
                case "'longtime'":
                    return info.LongTimePattern;
                case "'longdate'":
                    return info.LongDatePattern;
                case "'utc'":
                case "utc":
                    return "yyyy-MM-ddTHH:mm:ss.fffZ";
                default:
                    return format;
            }
        }

        private static string ReplaceWith24HourClock(string format)
        {
            format = Regex.Replace(format, "[hH]", "H");
            format = Regex.Replace(format, "t+", string.Empty);

            return format.Trim();
        }

        private static string ResolveDateTimeFormatAmbiguities(string format, DateTime dateTime, CultureInfo culture, CancellationToken cancellationToken)
        {
            var resultString = format;

            resultString = ReplaceDoubleQuotedStrings(resultString, out var replaceList, cancellationToken);
            resultString = TokenizeDatetimeFormat(resultString, cancellationToken);
            resultString = DetokenizeDatetimeFormat(resultString, dateTime, culture);
            resultString = RestoreDoubleQuotedStrings(resultString, replaceList, cancellationToken);

            return resultString;
        }

        private static string RestoreDoubleQuotedStrings(string format, List<string> replaceList, CancellationToken cancellationToken)
        {
            var stringReplaceRegex = new Regex("\u0011");
            var array = replaceList.ToArray();
            var index = 0;

            var match = stringReplaceRegex.Match(format);

            while (match.Success)
            {
                cancellationToken.ThrowIfCancellationRequested();

                format = format.Substring(0, match.Index) + array[index++].Replace("\"", string.Empty) + format.Substring(match.Index + match.Length);
                match = stringReplaceRegex.Match(format);
            }

            return format;
        }

        private static string ReplaceDoubleQuotedStrings(string format, out List<string> replaceList, CancellationToken cancellationToken)
        {
            var ret = string.Empty;

            replaceList = new List<string>();

            foreach (Match match in _internalStringRegex.Matches(format))
            {
                cancellationToken.ThrowIfCancellationRequested();

                replaceList.Add(match.Value);
            }

            return _internalStringRegex.Replace(format, "\u0011");
        }

        private static string DetokenizeDatetimeFormat(string format, DateTime dateTime, CultureInfo culture)
        {
            var hasAmPm = format.Contains('\u0001') || format.Contains('\u0002');

            // Day component            
            format = _daysDetokenizeRegex.Replace(format, dateTime.ToString("dddd", culture))
                          .Replace("\u0004\u0004\u0004", dateTime.ToString("ddd", culture))
                          .Replace("\u0004\u0004", dateTime.ToString("dd", culture))
                          .Replace("\u0004", dateTime.ToString("%d", culture));

            // Month component
            format = _monthsDetokenizeRegex.Replace(format, dateTime.ToString("MMMM", culture))
                          .Replace("\u0003\u0003\u0003", dateTime.ToString("MMM", culture))
                          .Replace("\u0003\u0003", dateTime.ToString("MM", culture))
                          .Replace("\u0003", dateTime.ToString("%M", culture));

            // Year component
            format = _yearsDetokenizeRegex.Replace(format, dateTime.ToString("yyyy", culture));
            format = _years2DetokenizeRegex.Replace(format, dateTime.ToString("yy", culture));

            // Hour component
            format = _hoursDetokenizeRegex.Replace(format, hasAmPm ? dateTime.ToString("hh", culture) : dateTime.ToString("HH", culture))
                          .Replace("\u0006", hasAmPm ? dateTime.ToString("%h", culture) : dateTime.ToString("%H", culture));

            // Minute component
            format = _minutesDetokenizeRegex.Replace(format, dateTime.ToString("mm", culture))
                          .Replace("\u000A", dateTime.ToString("%m", culture));

            // Second component
            format = _secondsDetokenizeRegex.Replace(format, dateTime.ToString("ss", culture))
                          .Replace("\u0008", dateTime.ToString("%s", culture));

            // Milliseconds component
            format = _milisecondsDetokenizeRegex.Replace(format, match =>
            {
                var len = match.Groups[0].Value.Length;
                var subSecondFormat = len == 1 ? "%f" : new string('f', len);
                return dateTime.ToString(subSecondFormat, culture);
            });

            // AM/PM component
            format = format.Replace("\u0001", dateTime.ToString("tt", culture))
                           .Replace("\u0002", dateTime.ToString("%t", culture).ToLowerInvariant());

            return format;
        }

        private static string TokenizeDatetimeFormat(string format, CancellationToken cancellationToken)
        {
            // Temporary replacements to avoid collisions with upcoming month names, etc.
            format = _ampmReplaceRegex.Replace(format, "\u0001");
            format = _apReplaceRegex.Replace(format, "\u0002");

            // Find all "m" chars for minutes, before seconds
            var match = _minutesBeforeSecondsRegex.Match(format);
            while (match.Success)
            {
                cancellationToken.ThrowIfCancellationRequested();

                format = format.Substring(0, match.Index) + "\u000A" + format.Substring(match.Index + 1);
                match = _minutesBeforeSecondsRegex.Match(format);
            }

            // Find all "m" chars for minutes, after hours
            match = _minutesAfterHoursRegex.Match(format);
            while (match.Success)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var afterHourFormat = format.Substring(match.Index);
                var minuteAfterHourPosition = _minutesRegex.Match(afterHourFormat);
                var pos = match.Index + minuteAfterHourPosition.Index;

                format = format.Substring(0, pos) + "\u000A" + format.Substring(pos + 1);

                match = _minutesAfterHoursRegex.Match(format);
            }

            var sb = new StringBuilder();
            foreach (var c in format)
            {
                cancellationToken.ThrowIfCancellationRequested();

                switch (c)
                {
                    case 'm': case 'M': sb.Append('\u0003'); break;
                    case 'd': case 'D': sb.Append('\u0004'); break;
                    case 'y': case 'Y': sb.Append('\u0005'); break;
                    case 'h': case 'H': sb.Append('\u0006'); break;
                    case 's': case 'S': sb.Append('\u0008'); break;
                    case '0': sb.Append('\u000E'); break;
                    default: sb.Append(c); break;
                }
            }

            return sb.ToString();
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-isblank-isempty
        // Take first non-blank value.
        public static async ValueTask<FormulaValue> Coalesce(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, FormulaValue[] args)
        {
            foreach (var arg in args)
            {
                runner.CheckCancel();

                var res = await runner.EvalArgAsync<ValidFormulaValue>(arg, context, arg.IRContext).ConfigureAwait(false);

                if (res.IsValue)
                {
                    var val = res.Value;
                    if (!(val is StringValue str && str.Value == string.Empty))
                    {
                        return res.ToFormulaValue();
                    }
                }

                if (res.IsError)
                {
                    return res.Error;
                }
            }

            return new BlankValue(irContext);
        }

        public static FormulaValue Lower(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, StringValue[] args)
        {
            return new StringValue(irContext, runner.CultureInfo.TextInfo.ToLower(args[0].Value));
        }

        public static FormulaValue Upper(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, StringValue[] args)
        {
            return new StringValue(irContext, runner.CultureInfo.TextInfo.ToUpper(args[0].Value));
        }

        public static FormulaValue EncodeUrl(IRContext irContext, StringValue[] args)
        {
            return new StringValue(irContext, Uri.EscapeDataString(args[0].Value));
        }

        public static FormulaValue Proper(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, StringValue[] args)
        {
            return new StringValue(irContext, runner.CultureInfo.TextInfo.ToTitleCase(runner.CultureInfo.TextInfo.ToLower(args[0].Value)));
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-len
        public static FormulaValue Len(IRContext irContext, StringValue[] args)
        {
            return new NumberValue(irContext, args[0].Value.Length);
        }

        // https://docs.microsoft.com/en-us/powerapps/maker/canvas-apps/functions/function-left-mid-right
        public static FormulaValue Mid(IRContext irContext, FormulaValue[] args)
        {
            var errors = new List<ErrorValue>();
            var start = (NumberValue)args[1];
            if (double.IsNaN(start.Value) || double.IsInfinity(start.Value) || start.Value <= 0)
            {
                errors.Add(CommonErrors.ArgumentOutOfRange(start.IRContext));
            }

            var count = (NumberValue)args[2];
            if (double.IsNaN(count.Value) || double.IsInfinity(count.Value) || count.Value < 0)
            {
                errors.Add(CommonErrors.ArgumentOutOfRange(count.IRContext));
            }

            if (errors.Count != 0)
            {
                return ErrorValue.Combine(irContext, errors);
            }

            TryGetInt(start, out int start1Based);
            var start0Based = start1Based - 1;

            string str = ((StringValue)args[0]).Value;
            if (str == string.Empty || start0Based >= str.Length)
            {
                return new StringValue(irContext, string.Empty);
            }

            TryGetInt(count, out int countValue);
            var minCount = Math.Min(countValue, str.Length - start0Based);
            var result = str.Substring(start0Based, minCount);

            return new StringValue(irContext, result);
        }

        public static FormulaValue Left(IRContext irContext, FormulaValue[] args)
        {
            return LeftOrRight(irContext, args, Left);
        }

        public static FormulaValue Right(IRContext irContext, FormulaValue[] args)
        {
            return LeftOrRight(irContext, args, Right);
        }

        private static string Left(string str, int i)
        {
            if (i >= str.Length)
            {
                return str;
            }

            return str.Substring(0, i);
        }

        private static string Right(string str, int i)
        {
            if (i >= str.Length)
            {
                return str;
            }

            return str.Substring(str.Length - i);
        }

        private static FormulaValue LeftOrRight(IRContext irContext, FormulaValue[] args, Func<string, int, string> leftOrRight)
        {
            if (args[0] is BlankValue || args[1] is BlankValue)
            {
                return new StringValue(irContext, string.Empty);
            }

            if (args[1] is not NumberValue count)
            {
                return CommonErrors.GenericInvalidArgument(irContext);
            }

            var source = (StringValue)args[0];

            if (count.Value < 0)
            {
                return CommonErrors.GenericInvalidArgument(irContext);
            }

            if ((count.Value % 1) != 0)
            {
                throw new NotImplementedException("Should have been handled by IR");
            }

            TryGetInt(count, out int intCount);

            return new StringValue(irContext, leftOrRight(source.Value, intCount));
        }

        private static FormulaValue Find(IRContext irContext, FormulaValue[] args)
        {
            var findText = (StringValue)args[0];
            var withinText = (StringValue)args[1];

            if (!TryGetInt(args[2], out int startIndexValue))
            {
                return CommonErrors.ArgumentOutOfRange(irContext);
            }

            if (startIndexValue < 1 || startIndexValue > withinText.Value.Length + 1)
            {
                return CommonErrors.ArgumentOutOfRange(irContext);
            }

            var index = withinText.Value.IndexOf(findText.Value, startIndexValue - 1, StringComparison.Ordinal);

            return index >= 0 ? new NumberValue(irContext, index + 1)
                              : new BlankValue(irContext);
        }

        private static FormulaValue Replace(IRContext irContext, FormulaValue[] args)
        {
            var source = ((StringValue)args[0]).Value;
            var start = ((NumberValue)args[1]).Value;
            var count = ((NumberValue)args[2]).Value;
            var replacement = ((StringValue)args[3]).Value;

            if (start <= 0 || count < 0)
            {
                return CommonErrors.ArgumentOutOfRange(irContext);
            }

            if (!TryGetInt(args[1], out int start1Based))
            {
                start1Based = source.Length + 1;
            }

            var start0Based = start1Based - 1;
            var prefix = start0Based < source.Length ? source.Substring(0, start0Based) : source;

            if (!TryGetInt(args[2], out int intCount))
            {
                intCount = intCount - start0Based;
            }

            var suffixIndex = start0Based + intCount;
            var suffix = suffixIndex < source.Length ? source.Substring(suffixIndex) : string.Empty;
            var result = prefix + replacement + suffix;

            return new StringValue(irContext, result);
        }

        public static FormulaValue Split(IRContext irContext, StringValue[] args)
        {
            var text = args[0].Value;
            var separator = args[1].Value;

            // The separator can be zero, one, or more characters that are matched as a whole in the text string. Using a zero length or blank
            // string results in each character being broken out individually.
            var substrings = string.IsNullOrEmpty(separator) ? text.Select(c => new string(c, 1)) : text.Split(new string[] { separator }, StringSplitOptions.None);
            var rows = substrings.Select(s => new StringValue(IRContext.NotInSource(FormulaType.String), s));

            return new InMemoryTableValue(irContext, StandardTableNodeRecords(irContext, rows.ToArray(), forceSingleColumn: true));
        }

        // This is static analysis before actually executing, so just use string lengths and avoid contents. 
        internal static int SubstituteGetResultLength(int sourceLen, int matchLen, int replacementLen, bool replaceAll)
        {
            int maxLenChars;

            if (matchLen > sourceLen)
            {
                // Match is too large, can't be found.
                // So will not match and just return original.
                return sourceLen;
            }

            if (replaceAll)
            {
                // Replace all instances. 
                // Maximum possible length of Substitute, convert all the Match to Replacement. 
                // Unicode, so 2B per character.
                if (matchLen == 0)
                {
                    maxLenChars = sourceLen;
                }
                else
                {
                    // Round up as conservative estimate. 
                    maxLenChars = (int)Math.Ceiling((double)sourceLen / matchLen) * replacementLen;
                }
            }
            else
            {
                // Only replace 1 instance 
                maxLenChars = sourceLen - matchLen + replacementLen;
            }

            // If not match found, will still be source length 
            return Math.Max(sourceLen, maxLenChars);
        }

        private static FormulaValue Substitute(EvalVisitor runner, EvalVisitorContext context, IRContext irContext, FormulaValue[] args)
        {
            var source = (StringValue)args[0];
            var match = (StringValue)args[1];
            var replacement = (StringValue)args[2];

            var instanceNum = -1;
            if (args.Length > 3)
            {
                var nv = (NumberValue)args[3];
                if (nv.Value > source.Value.Length)
                {
                    return source;
                }

                TryGetInt(nv, out instanceNum);
            }

            // Compute max possible memory this operation may need.
            var sourceLen = source.Value.Length;
            var matchLen = match.Value.Length;
            var replacementLen = replacement.Value.Length;

            var maxLenChars = SubstituteGetResultLength(sourceLen, matchLen, replacementLen, instanceNum < 0);
            runner.Governor.CanAllocateString(maxLenChars);

            var result = SubstituteWorker(runner, irContext, source, match, replacement, instanceNum);

            Contracts.Assert(result.Value.Length <= maxLenChars);

            return result;
        }

        private static StringValue SubstituteWorker(EvalVisitor eval, IRContext irContext, StringValue source, StringValue match, StringValue replacement, int instanceNum)
        {
            if (string.IsNullOrEmpty(match.Value))
            {
                return source;
            }

            StringBuilder strBuilder = new StringBuilder(source.Value);
            if (instanceNum < 0)
            {
                strBuilder.Replace(match.Value, replacement.Value);
            }
            else
            {
                // 0 is an error. This was already enforced by the IR
                Contract.Assert(instanceNum > 0);

                for (int idx = 0; idx < source.Value.Length; idx += match.Value.Length)
                {
                    eval.CheckCancel();

                    idx = source.Value.IndexOf(match.Value, idx, StringComparison.Ordinal);
                    if (idx == -1)
                    {
                        break;
                    }

                    if (--instanceNum == 0)
                    {
                        strBuilder.Replace(match.Value, replacement.Value, idx, match.Value.Length);
                        break;
                    }
                }
            }

            return new StringValue(irContext, strBuilder.ToString());
        }

        public static FormulaValue StartsWith(IRContext irContext, StringValue[] args)
        {
            var text = args[0];
            var start = args[1];

            return new BooleanValue(irContext, text.Value.StartsWith(start.Value, StringComparison.OrdinalIgnoreCase));
        }

        public static FormulaValue EndsWith(IRContext irContext, StringValue[] args)
        {
            var text = args[0];
            var end = args[1];

            return new BooleanValue(irContext, text.Value.EndsWith(end.Value, StringComparison.OrdinalIgnoreCase));
        }

        public static FormulaValue Trim(IRContext irContext, StringValue[] args)
        {
            var text = args[0];

            // Remove all whitespace except ASCII 10, 11, 12, 13 and 160, then trim to follow Excel's behavior
            var regex = new Regex(@"[^\S\xA0\n\v\f\r]+");

            var result = regex.Replace(text.Value, " ").Trim();

            return new StringValue(irContext, result);
        }

        public static FormulaValue TrimEnds(IRContext irContext, StringValue[] args)
        {
            var text = args[0];

            var result = text.Value.Trim();

            return new StringValue(irContext, result);
        }

        public static FormulaValue Guid(IRContext irContext, StringValue[] args)
        {
            var text = args[0].Value;
            try
            {
                var guid = new Guid(text);

                return new GuidValue(irContext, guid);
            }
            catch
            {
                return CommonErrors.GenericInvalidArgument(irContext);
            }
        }

        public static FormulaValue OptionSetValueToLogicalName(IRContext irContext, OptionSetValue[] args)
        {
            var optionSet = args[0];
            var logicalName = optionSet.Option;
            return new StringValue(irContext, logicalName);
        }

        private static DateTime ConvertToUTC(DateTime dateTime, TimeZoneInfo fromTimeZone)
        {
            var resultDateTime = new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified), fromTimeZone.GetUtcOffset(dateTime));
            return resultDateTime.UtcDateTime;
        }

        internal static bool TryGetInt(FormulaValue value, out int outputValue)
        {
            double inputValue;
            outputValue = int.MinValue;

            switch (value)
            {
                case NumberValue n:
                    inputValue = n.Value;
                    break;
                case DecimalValue w:
                    inputValue = (double)w.Value;
                    break;
                default:
                    return false;
            }

            if (inputValue > int.MaxValue)
            {
                outputValue = int.MaxValue;
                return false;
            }
            else if (inputValue < int.MinValue)
            {
                outputValue = int.MinValue;
                return false;
            }

            outputValue = (int)inputValue;
            return true;
        }
    }
}
