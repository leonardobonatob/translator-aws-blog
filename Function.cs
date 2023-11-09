    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Text.Json;
    using Amazon.S3;
    using Amazon.Lambda.APIGatewayEvents;
    using CsQuery;
    using CsQuery.ExtensionMethods;
    using Microsoft.FSharp.Linq.RuntimeHelpers;
    using System.Threading.Tasks;
    using Amazon.Lambda.Core;
    using Amazon.Translate;
    using static System.FormattableString;
    using Amazon.S3.Model;
    using Amazon.Translate.Model;
    using Amazon.IdentityManagement;
    using Amazon.IdentityManagement.Model;


// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace HtmltoWord {

    internal partial class Html
    {
        internal Html(string title, IEnumerable<IGrouping<string, Tuple<string, string>>> chapters)
        {
            this.Title = title;
            this.Chapters = chapters;
        }

        internal string Title { get; }

        internal IEnumerable<IGrouping<string, Tuple<string, string>>> Chapters { get; }
    }

        internal class InputJson
        {
            public object body { get; set; }
        }


        internal class InputLambda
        {
            public string source { get; set; }
            public string sourceLanguageCode { get; set; }
            public string targetLanguageCode { get; set; }
        }

    public class Function
    {

        public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(Object input, ILambdaContext context)
        {
            return await FuncEval(input);
        }

        public static async Task<APIGatewayHttpApiV2ProxyResponse> FuncEval(Object input)
        {
                string outputbucket = Environment.GetEnvironmentVariable("ROSETTA_TEMP_BUCKET");
                
                InputJson? inputJObject = JsonSerializer.Deserialize<InputJson>(input.ToString());
        
                InputLambda? inputObject = JsonSerializer.Deserialize<InputLambda>(inputJObject.body.ToString());

                string source = inputObject.source;
                string sourcelanguage = inputObject.sourceLanguageCode;
                string targetlanguage = inputObject.targetLanguageCode;
                //string source = inputJObject["source"].ToString();
                //string sourcelanguage = inputJObject["sourceLanguageCode"].ToString();
                //string targetlanguage = inputJObject["targetLanguageCode"].ToString();
                Console.WriteLine($"\nCreating HTML document");
                Html html = DownloadHtml(source);

                Console.WriteLine($"\nCreating Temp file");
                string tempHtmlFile = CreateTempFile(html);

                Console.WriteLine($"Saving {targetlanguage} document inside {outputbucket}");
                APIGatewayHttpApiV2ProxyResponse docInsert = await SaveDocument(source,  sourcelanguage, targetlanguage, outputbucket, tempHtmlFile);

                return docInsert;
        }

        private static Html DownloadHtml(
            string indexUrl, int downloadThreadPerProcessor = 10)
        {
            using (WebClient indexClient = new WebClient())
            {
                indexClient.Encoding = Encoding.UTF8;
                Console.WriteLine(Invariant($"Downloading {indexUrl}."));
                CQ indexPage = indexClient.DownloadString(indexUrl);

                //article class="blog-post content-item">
                CQ article = indexPage["article.blog-post"];

                IEnumerable<IGrouping<string, Tuple<string, string>>> chapters = article
                    .Select(chapter => chapter.Cq())
                    .AsParallel()
                    .AsOrdered()
                    .WithDegreeOfParallelism(Environment.ProcessorCount * downloadThreadPerProcessor)

                    .Select(chapter =>
                    {
                        Tuple<string, string>[] sections = chapter.Find("section.blog-post-content")[0].Cq()
                          .Select(section => section.Cq())
                          .AsParallel()
                          .AsOrdered()
                          .WithDegreeOfParallelism(Environment.ProcessorCount * downloadThreadPerProcessor)
                          .Select(section =>
                          {
                              string sectionUrl = section.Attr<string>("h2");
                              CQ sectionArticle = "";
                              if (sectionUrl == null)
                              {
                                  sectionArticle = indexPage["section.blog-post-content"];
                                  //sectionArticle.Children("header").Remove();
                                  Enumerable
                                      .Range(1, 7)
                                      .Reverse()
                                      .ForEach(i => sectionArticle
                                              .Find(Invariant($"h{i}")).Contents().Unwrap()
                                              .Wrap(Invariant($"<h{i + 1}/>"))
                                              .Parent()
                                              .Find("a").Contents().Unwrap());
                                  sectionArticle.Find("pre span").Css("background", string.Empty);
                                  sectionArticle.Find("p,h2,ol,h3")
                                      .Select(paragraph => paragraph.Cq())
                                      .ForEach(paragraph =>
                                      {
                                          string paragraphText = paragraph.Text().Trim();
                                          if ((paragraph.Children().Length == 0 &&
                                                   string.IsNullOrWhiteSpace(paragraphText)))
                                          {
                                              paragraph.Remove();
                                          }
                                      });
                                  return Tuple.Create("", sectionArticle.Html());
                              }
                              else
                              {
                                  return Tuple.Create(section.Text().Trim(), sectionArticle.Html());
                              }

                          })
                          .ToArray();
                        Tuple<string, string>[] footers = chapter.Find("footer")[1].Cq()
                            .Select(footer => footer.Cq())
                            .AsParallel()
                            .AsOrdered()
                            .WithDegreeOfParallelism(Environment.ProcessorCount * downloadThreadPerProcessor)
                            .Select(footer =>
                            {
                                string sectionUrl = footer.Attr<string>("h2");
                                CQ footerArticle = "";
                                if (sectionUrl == null && footer.Find("div.blog-author-box").ToString() != "")
                                {
                                    footerArticle = footer;
                                    Enumerable
                                        .Range(1, 7)
                                        .Reverse()
                                        .ForEach(i => footerArticle
                                                .Find(Invariant($"h{i}")).Contents().Unwrap()
                                                .Wrap(Invariant($"<h{i + 1}/>"))
                                                .Parent()
                                                .Find("a").Contents().Unwrap());
                                    footerArticle.Find("pre span").Css("background", string.Empty);
                                    footerArticle.Find("p,h2,ol,h3")
                                        .Select(paragraph => paragraph.Cq())
                                        .ForEach(paragraph =>
                                        {
                                            string paragraphText = paragraph.Text().Trim();
                                            if ((paragraph.Children().Length == 0 &&
                                                     string.IsNullOrWhiteSpace(paragraphText)))
                                            {
                                                paragraph.Remove();
                                            }
                                        });
                                    return Tuple.Create(sections[0].ToString().Replace("(,",""), footerArticle.Html());
                                }
                                else
                                {
                                    return Tuple.Create(sections[0].ToString().Replace("(,", ""), footerArticle.Html());
                                }                                
                            })
                            .ToArray();
                        return new Grouping<string, Tuple<string, string>>(
                            chapter.Find("h1").Html(),
                            footers);
                    })
                    .ToArray();

                return new Html(
                    indexPage["title"].Text().Trim(),
                    chapters);
            }
        }

        private static string CreateTempFile(Html html)
        {
            string tempHtmlFile = Path.ChangeExtension(Path.GetTempFileName(), "doc");
            string htmlContent = html.TransformText();

            Console.WriteLine(Invariant($"Saving HTML as {tempHtmlFile}, {htmlContent.Length}."));
            File.WriteAllText(tempHtmlFile, htmlContent);
            //Texto escrito para dentro do doc temp

            return tempHtmlFile;
        }
        
        private static async Task<APIGatewayHttpApiV2ProxyResponse> SaveDocument(string source, string sourcelanguage, string targetlanguage, string output,string tempHtmlFile)
        {
                var s3Client = new AmazonS3Client();

                string sourceoutput = source.Replace("https://aws.amazon.com/","").Replace("blogs/","").Replace("/","-");
                string bucketoutput = $"{output}/Blog/{sourceoutput}";

                Console.WriteLine($"\nPutting object inside bucket {output}...");

                PutObjectRequest requestPUT = new PutObjectRequest
                {
                    BucketName = bucketoutput,
                    Key = $"{sourceoutput}.doc",
                    FilePath = tempHtmlFile
                };

                var putResponse = await s3Client.PutObjectAsync(requestPUT);
                if (putResponse.HttpStatusCode == System.Net.HttpStatusCode.OK)
                {
                    Console.WriteLine($"Success! Document saved in {output}");
                }
                else if(putResponse.HttpStatusCode != System.Net.HttpStatusCode.OK)
                {
                    Console.WriteLine($"Put object request response: {putResponse.HttpStatusCode}");
                    Console.WriteLine("check your access to the desired bucket.");
                    return new APIGatewayHttpApiV2ProxyResponse
                    {
                        Body = "The request PutObject to the desired bucket was failed. Check your logs.",
                        StatusCode = 400
                    };
                }

                return await TranslateDocument(sourceoutput, sourcelanguage, targetlanguage, output);   
        }

        private static async Task<APIGatewayHttpApiV2ProxyResponse>TranslateDocument(string sourceoutput, string sourcelanguage, string targetlanguage, string output)
        {
            var translate = new AmazonTranslateClient();

            var contentType = "text/html";

            var s3InputUri = $"s3://{output}/Blog/{sourceoutput}/";


            var iamClient = new AmazonIdentityManagementServiceClient();



            var inputConfig = new InputDataConfig
            {
                ContentType = contentType,
                S3Uri = s3InputUri
            };

            var outputConfig = new OutputDataConfig
            {
                S3Uri = $"s3://{output}/Blog/Translation - {sourceoutput}"
            };

            try
            {
                await iamClient.GetRoleAsync(new GetRoleRequest { RoleName = "translate-s3-access-role" });
            }
            catch (Exception erro)
            {
                Console.WriteLine(erro);
                Console.WriteLine("Criar IAM Role para o Amazon Translate com seguinte estrutura:" +
                    "Relationships: \"Service\": \"translate.amazonaws.com\"  \"Action\": \"sts:AssumeRole\" " +
                    "\n Permissions: \"Action\": [\r\n           \"s3:*\",\r\n                \"s3-object-lambda:*\",\r\n                \"translate:*\",\r\n                \"cloudwatch:*\" ");
            }


            Console.WriteLine($"Creating Translation for {targetlanguage} targetlanguage");
            var request = new StartTextTranslationJobRequest()
            {
                JobName = $"{sourceoutput}Translation",
                DataAccessRoleArn = iamClient.GetRoleAsync(new GetRoleRequest { RoleName = "translate-s3-access-role" }).Result.Role.Arn,
                InputDataConfig = inputConfig,
                OutputDataConfig = outputConfig,
                SourceLanguageCode = sourcelanguage,
                TargetLanguageCodes = new List<string> { targetlanguage }
            };
            var outputs3file = outputConfig.S3Uri.Replace($"s3://{output}/","") + $"/{targetlanguage}.{sourceoutput}.doc";
            var response = await translate.StartTextTranslationJobAsync(request);

            if (response.HttpStatusCode != System.Net.HttpStatusCode.OK)
            {
                Console.WriteLine($"{response.JobId}: {response.JobStatus}");
                return new APIGatewayHttpApiV2ProxyResponse
                {
                    Body = "TranslationJob failed. Check output logs.",
                    StatusCode = 400
                };
            }
            else { 
                object message = JsonSerializer.Serialize(new {id = response.JobId, filename =  $"{targetlanguage}.{sourceoutput}.doc", s3Path = outputs3file, sourceLanguageCode = sourcelanguage, targetLanguageCode = targetlanguage, status = "RUNNING" });

                return new APIGatewayHttpApiV2ProxyResponse
                {
                    Body = message.ToString(),
                    StatusCode = 200,
                    Headers = new Dictionary<string, string> { { "Content-Type","application/json" }, {"Access-Control-Allow-Headers","*"}, {"Access-Control-Allow-Origin", "*"}, {"Access-Control-Allow-Methods", "*"}  }
                };
            }
        }
    }

}
